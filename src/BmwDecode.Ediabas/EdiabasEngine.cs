#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BmwDecode.Core.Diagnostics;
using EdiabasLib;
using LogLevel = BmwDecode.Core.Diagnostics.LogLevel;

namespace BmwDecode.Ediabas;

/// <summary>
/// Real-vehicle engine: runs SGBD jobs through EdiabasLib over a K+DCAN cable (COM port or FTDI) or ENET.
/// All EDIABAS calls run on one dedicated worker so the bus only ever carries one job.
/// </summary>
public sealed partial class EdiabasEngine : IDiagnosticEngine
{
    private readonly SemaphoreSlim _bus = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _variantCache = new(StringComparer.OrdinalIgnoreCase);
    private EdiabasNet? _ediabas;
    private volatile bool _abort;
    private ConnectionSettings? _settings;

    static EdiabasEngine()
    {
        // SGBD texts are Windows-1252; .NET Core needs the code page provider for that.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public string Name => _settings is null ? "EDIABAS" : $"EDIABAS · {Describe(_settings)}";
    public bool IsConnected => _ediabas?.EdInterfaceClass?.Connected == true;
    public bool IsSimulation => false;

    public event EventHandler<DiagnosticLogEntry>? Log;

    public async Task ConnectAsync(ConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(settings.EcuPath))
        {
            throw new DiagnosticException($"EDIABAS ECU folder not found: {settings.EcuPath}", "BMWDECODE_ECU_PATH");
        }

        await DisconnectAsync().ConfigureAwait(false);
        await RunAsync(() =>
        {
            var ediabas = new EdiabasNet
            {
                EdInterfaceClass = CreateInterface(settings),
                AbortJobFunc = () => _abort,
            };
            ediabas.SetConfigProperty("EcuPath", settings.EcuPath);
            if (settings.Trace)
            {
                var tracePath = settings.TracePath ?? Path.Combine(Path.GetTempPath(), "bmwDecode", "trace");
                Directory.CreateDirectory(tracePath);
                ediabas.SetConfigProperty("TracePath", tracePath);
                ediabas.SetConfigProperty("IfhTrace", "2");
                ediabas.SetConfigProperty("ApiTrace", "1");
            }

            Write(LogLevel.Info, $"Opening {Describe(settings)} (EcuPath {settings.EcuPath})");
            bool connected;
            EdiabasNet.ErrorCodes code;
            try
            {
                // EdiabasLib either returns false or throws, depending on where opening the port fails.
                connected = ediabas.EdInterfaceClass.InterfaceConnect();
                code = ediabas.ErrorCodeLast;
            }
            catch (EdiabasNet.EdiabasNetException ex)
            {
                connected = false;
                code = ex.ErrorCode;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or DllNotFoundException)
            {
                ediabas.Dispose();
                throw new DiagnosticException($"Could not open {Describe(settings)}: {ex.Message}", "EDIABAS_IFH_0018", ex);
            }

            if (!connected)
            {
                ediabas.Dispose();
                throw new DiagnosticException(
                    $"Could not open {Describe(settings)}: {EdiabasNet.GetErrorDescription(code)}. " +
                    "Check the cable is plugged in, the port is right and the OBD socket has 12 V.",
                    code.ToString());
            }

            _ediabas = ediabas;
            _settings = settings;
            _variantCache.Clear();
            Write(LogLevel.Info, $"Interface open: {Describe(settings)}");
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        if (_ediabas is null)
        {
            return;
        }

        await RunAsync(() =>
        {
            try
            {
                _ediabas?.EdInterfaceClass?.InterfaceDisconnect();
            }
            finally
            {
                _ediabas?.Dispose();
                _ediabas = null;
                _variantCache.Clear();
            }

            Write(LogLevel.Info, "Interface closed");
            return true;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    public Task<string> ResolveVariantAsync(string sgbd, CancellationToken cancellationToken = default) =>
        RunAsync(() => ResolveLocked(sgbd), cancellationToken);

    public Task<JobResult> ExecuteAsync(string sgbd, string job, string args = "", CancellationToken cancellationToken = default) =>
        RunAsync(() =>
        {
            var ediabas = RequireConnection();
            var variant = ResolveLocked(sgbd);
            var sw = Stopwatch.StartNew();
            try
            {
                ediabas.ArgBinary = null;
                ediabas.ArgBinaryStd = null;
                ediabas.ArgString = args ?? string.Empty;
                ediabas.ResultsRequests = string.Empty;
                ediabas.ExecuteJob(job);
            }
            catch (EdiabasNet.EdiabasNetException ex)
            {
                Write(LogLevel.Warning, $"{variant}.{job}({args}) failed: {EdiabasNet.GetErrorDescription(ex.ErrorCode)}");
                throw new DiagnosticException(EdiabasNet.GetErrorDescription(ex.ErrorCode), ex.ErrorCode.ToString(), ex);
            }

            var result = ResultConverter.Convert(sgbd, variant, job, args ?? string.Empty, ediabas.ResultSets, sw.Elapsed);
            Write(LogLevel.Debug, $"{variant}.{job}({args}) -> {result.JobStatus ?? "?"} [{result.Sets.Count} sets, {sw.ElapsedMilliseconds} ms]");
            return result;
        }, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        // The bus semaphore is deliberately not disposed: a quick test or live poll may still be waiting on it.
        await DisconnectAsync().ConfigureAwait(false);
    }

    private string ResolveLocked(string sgbd)
    {
        var ediabas = RequireConnection();
        if (_variantCache.TryGetValue(sgbd, out var cached))
        {
            ediabas.ResolveSgbdFile(cached);
            return cached;
        }

        try
        {
            // For a group file (D_MOTOR) EdiabasLib runs the identification job on the car and loads the matching variant.
            ediabas.ResolveSgbdFile(sgbd);
        }
        catch (EdiabasNet.EdiabasNetException ex)
        {
            Write(LogLevel.Debug, $"{sgbd}: {EdiabasNet.GetErrorDescription(ex.ErrorCode)}");
            throw new DiagnosticException(EdiabasNet.GetErrorDescription(ex.ErrorCode), ex.ErrorCode.ToString(), ex);
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or ArgumentException)
        {
            throw new DiagnosticException($"SGBD {sgbd} not found in {_settings?.EcuPath}: {ex.Message}", "EDIABAS_SYS_0005", ex);
        }

        var variant = Path.GetFileNameWithoutExtension(ediabas.SgbdFileName ?? sgbd).ToUpperInvariant();
        _variantCache[sgbd] = variant;
        if (!variant.Equals(sgbd, StringComparison.OrdinalIgnoreCase))
        {
            Write(LogLevel.Info, $"{sgbd.ToUpperInvariant()} identified as {variant}");
        }

        return variant;
    }

    private EdiabasNet RequireConnection() =>
        _ediabas ?? throw new DiagnosticException("Not connected. Connect to the vehicle first.", "EDIABAS_IFH_0018");

    /// <summary>Runs blocking EDIABAS work on a worker thread, one job at a time, honouring cancellation.</summary>
    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        await _bus.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _abort = false;
            using var registration = cancellationToken.Register(() => _abort = true);
            var result = await Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (DiagnosticException) when (_abort && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (EdiabasNet.EdiabasNetException ex)
        {
            // Safety net: never let a raw EdiabasLib exception reach the UI.
            if (_abort && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new DiagnosticException(EdiabasNet.GetErrorDescription(ex.ErrorCode), ex.ErrorCode.ToString(), ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not DiagnosticException)
        {
            // EdiabasLib reports aborts ("executeJob aborted") and many interpreter failures as plain Exception.
            if (_abort && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            var inner = ex.InnerException is EdiabasNet.EdiabasNetException en ? en : null;
            throw inner is not null
                ? new DiagnosticException(EdiabasNet.GetErrorDescription(inner.ErrorCode), inner.ErrorCode.ToString(), ex)
                : new DiagnosticException(ex.InnerException?.Message ?? ex.Message, "EDIABAS_SYS_0000", ex);
        }
        finally
        {
            _abort = false;
            _bus.Release();
        }
    }

    private void Write(LogLevel level, string message) => Log?.Invoke(this, DiagnosticLogEntry.Now(level, message));

    internal static string Describe(ConnectionSettings s) => s.Kind switch
    {
        InterfaceKind.KDcanSerial => $"K+DCAN on {s.Port}",
        InterfaceKind.Enet => $"ENET ({s.Port})",
        _ => s.Kind.ToString(),
    };
}

/// <summary>Converts EdiabasLib result sets into the Core model.</summary>
internal static class ResultConverter
{
    public static JobResult Convert(string sgbd, string variant, string job, string args,
        List<Dictionary<string, EdiabasNet.ResultData>>? sets, TimeSpan duration)
    {
        sets ??= [];
        var system = sets.Count > 0 ? ToSet(sets[0]) : new ResultSet([]);
        var jobSets = sets.Skip(1).Select(ToSet).ToList();
        return new JobResult(sgbd, variant, job, args, system, jobSets, duration);
    }

    public static ResultSet ToSet(Dictionary<string, EdiabasNet.ResultData> dict) =>
        new(dict.Values.Select(ToValue));

    private static ResultValue ToValue(EdiabasNet.ResultData data)
    {
        var kind = data.ResType switch
        {
            EdiabasNet.ResultType.TypeR => ResultKind.Real,
            EdiabasNet.ResultType.TypeS => ResultKind.Text,
            EdiabasNet.ResultType.TypeY => ResultKind.Binary,
            _ => ResultKind.Integer,
        };

        // Integer values arrive as Int64; anything unexpected is kept as text rather than lost.
        if (kind == ResultKind.Integer && data.OpData is not (long or int or short or byte or ulong or uint or ushort or sbyte))
        {
            kind = data.OpData is double ? ResultKind.Real : ResultKind.Text;
        }

        return new ResultValue(data.Name, kind, data.OpData);
    }
}
