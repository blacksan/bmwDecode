using System.Diagnostics;
using BmwDecode.Core.Diagnostics;

namespace BmwDecode.Core.Simulation;

/// <summary>
/// Demo vehicle: an E84 X1 with an M57 diesel (DDE6) and an E90 CAS, RWD (no transfer case).
/// Lets the whole application be exercised without a car or cable. All data is fictional.
/// </summary>
public sealed class SimulatedEngine : IDiagnosticEngine, ISgbdInspector
{
    public const string DemoVin = "WBADEM00000E84X10";

    private readonly SemaphoreSlim _bus = new(1, 1);
    private readonly Random _random;
    private readonly Dictionary<string, SimEcu> _ecus;
    private readonly bool _fast;
    private bool _connected;

    /// <param name="fast">Skip artificial bus delays (unit tests).</param>
    /// <param name="seed">Random seed for reproducible live data.</param>
    public SimulatedEngine(bool fast = false, int seed = 84)
    {
        _fast = fast;
        _random = new Random(seed);
        _ecus = SimEcu.CreateE84().ToDictionary(e => e.Variant, StringComparer.OrdinalIgnoreCase);
    }

    public string Name => "Demo vehicle (simulated E84)";
    public bool IsConnected => _connected;
    public bool IsSimulation => true;
    public bool IsAvailable => true;

    public event EventHandler<DiagnosticLogEntry>? Log;

    public async Task ConnectAsync(ConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        await DelayAsync(300, cancellationToken).ConfigureAwait(false);
        _connected = true;
        Write(Diagnostics.LogLevel.Info, "Connected to simulated E84 (demo mode).");
    }

    public Task DisconnectAsync()
    {
        _connected = false;
        Write(Diagnostics.LogLevel.Info, "Disconnected from demo vehicle.");
        return Task.CompletedTask;
    }

    public async Task<string> ResolveVariantAsync(string sgbd, CancellationToken cancellationToken = default)
    {
        await _bus.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            await DelayAsync(60, cancellationToken).ConfigureAwait(false);
            var ecu = Find(sgbd);
            if (ecu is null)
            {
                await DelayAsync(180, cancellationToken).ConfigureAwait(false);
                Write(Diagnostics.LogLevel.Debug, $"{sgbd}: no response");
                throw new DiagnosticException("IFH-0009: NO RESPONSE FROM CONTROLUNIT", "EDIABAS_IFH_0009");
            }

            Write(Diagnostics.LogLevel.Debug, $"{sgbd} -> {ecu.Variant}");
            return ecu.Variant;
        }
        finally
        {
            _bus.Release();
        }
    }

    public async Task<JobResult> ExecuteAsync(string sgbd, string job, string args = "", CancellationToken cancellationToken = default)
    {
        await _bus.WaitAsync(cancellationToken).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        try
        {
            EnsureConnected();
            await DelayAsync(40 + _random.Next(60), cancellationToken).ConfigureAwait(false);

            if (sgbd.Equals("FA", StringComparison.OrdinalIgnoreCase) && job.Equals("FA_STREAM2STRUCT", StringComparison.OrdinalIgnoreCase))
            {
                return Result(sgbd, "FA", job, args, sw, [Set(("BR", "E84"), ("JOB_STATUS", "OKAY"))]);
            }

            if (sgbd.Equals("UTILITY", StringComparison.OrdinalIgnoreCase))
            {
                var utility = job.ToUpperInvariant() switch
                {
                    "STATUS_UBATT" => Set(("STAT_UBATT", 12_400L + _random.Next(300)), ("JOB_STATUS", "OKAY")),
                    "STATUS_ZUENDUNG" => Set(("STAT_ZUENDUNG", 1L), ("JOB_STATUS", "OKAY")),
                    _ => Set(("JOB_STATUS", "ERROR_JOB")),
                };
                return Result(sgbd, "UTILITY", job, args, sw, [utility]);
            }

            var ecu = Find(sgbd) ?? throw new DiagnosticException("IFH-0009: NO RESPONSE FROM CONTROLUNIT", "EDIABAS_IFH_0009");
            var sets = ecu.Run(job.ToUpperInvariant(), args, _random);
            Write(Diagnostics.LogLevel.Debug, $"{ecu.Variant}.{job}({args}) -> {sets.LastOrDefault()?.Text("JOB_STATUS")}");
            return Result(sgbd, ecu.Variant, job, args, sw, sets);
        }
        finally
        {
            _bus.Release();
        }
    }

    public IReadOnlyList<SgbdFile> ListFiles() =>
        _ecus.Values.Select(e => new SgbdFile(e.Variant, false, e.Variant))
            .Concat(_ecus.Values.Select(e => new SgbdFile(e.Group, true, e.Group)))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public Task<SgbdDescription> DescribeAsync(string sgbd, CancellationToken cancellationToken = default)
    {
        var ecu = Find(sgbd) ?? throw new DiagnosticException($"SYS-0005: OBJECT FILE NOT FOUND ({sgbd})", "EDIABAS_SYS_0005");
        return Task.FromResult(new SgbdDescription(ecu.Variant, ecu.Jobs.Select(j => j.Value).ToList()));
    }

    public Task<JobDescription> DescribeJobAsync(string sgbd, string job, CancellationToken cancellationToken = default)
    {
        var ecu = Find(sgbd) ?? throw new DiagnosticException($"SYS-0005: OBJECT FILE NOT FOUND ({sgbd})", "EDIABAS_SYS_0005");
        return ecu.Jobs.TryGetValue(job, out var d)
            ? Task.FromResult(d)
            : throw new DiagnosticException($"SYS-0008: JOB NOT FOUND ({job})", "EDIABAS_SYS_0008");
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        _bus.Dispose();
        return ValueTask.CompletedTask;
    }

    private SimEcu? Find(string sgbd) =>
        _ecus.TryGetValue(sgbd, out var ecu)
            ? ecu
            : _ecus.Values.FirstOrDefault(e => e.Group.Equals(sgbd, StringComparison.OrdinalIgnoreCase) ||
                                               e.Aliases.Contains(sgbd, StringComparer.OrdinalIgnoreCase));

    private void EnsureConnected()
    {
        if (!_connected)
        {
            throw new DiagnosticException("IFH-0018: INITIALIZATION ERROR (not connected)", "EDIABAS_IFH_0018");
        }
    }

    private Task DelayAsync(int ms, CancellationToken ct) => _fast ? Task.CompletedTask : Task.Delay(ms, ct);

    private void Write(Diagnostics.LogLevel level, string message) => Log?.Invoke(this, DiagnosticLogEntry.Now(level, message));

    private static JobResult Result(string sgbd, string variant, string job, string args, Stopwatch sw, IReadOnlyList<ResultSet> sets)
    {
        var system = Set(("VARIANTE", variant), ("OBJECT", variant.ToLowerInvariant()), ("JOBNAME", job), ("SAETZE", (long)sets.Count),
            ("UBATTCURRENT", 1L), ("IGNITIONCURRENT", 1L));
        return new JobResult(sgbd, variant, job, args, system, sets, sw.Elapsed);
    }

    internal static ResultSet Set(params (string Name, object Value)[] values) =>
        new(values.Select(v => v.Value switch
        {
            string s => new ResultValue(v.Name, ResultKind.Text, s),
            double d => new ResultValue(v.Name, ResultKind.Real, d),
            byte[] b => new ResultValue(v.Name, ResultKind.Binary, b),
            _ => new ResultValue(v.Name, ResultKind.Integer, Convert.ToInt64(v.Value, System.Globalization.CultureInfo.InvariantCulture)),
        }));
}
