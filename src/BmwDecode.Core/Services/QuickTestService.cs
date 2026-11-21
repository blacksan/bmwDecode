using BmwDecode.Core.Catalog;
using BmwDecode.Core.Diagnostics;

namespace BmwDecode.Core.Services;

public enum EcuScanState
{
    Pending,
    Scanning,
    NoFaults,
    Faults,
    NotFitted,
    Error,
}

/// <summary>Outcome of the quick test for one ECU slot.</summary>
public sealed record EcuScanResult(
    EcuDefinition Ecu,
    EcuScanState State,
    string? Variant = null,
    IReadOnlyList<IdentItem>? Ident = null,
    IReadOnlyList<FaultEntry>? Faults = null,
    string? Error = null)
{
    public IReadOnlyList<FaultEntry> FaultList => Faults ?? [];
    public bool IsFitted => Variant is not null;

    public string? PartNumber => Ident?.FirstOrDefault(i => i.Key.Equals("ID_BMW_NR", StringComparison.OrdinalIgnoreCase))?.Value;
}

public sealed record QuickTestReport(
    string Series,
    VehicleIdentity Vehicle,
    DateTimeOffset Started,
    TimeSpan Duration,
    IReadOnlyList<EcuScanResult> Results,
    bool WasCancelled)
{
    /// <summary>How the module list was obtained ("reported by the car via E89X" or "probed 66 slots").</summary>
    public string? DiscoveryNote { get; init; }

    public IEnumerable<EcuScanResult> Fitted => Results.Where(r => r.IsFitted);
    public int FaultCount => Results.Sum(r => r.FaultList.Count);
}

public sealed record QuickTestOptions
{
    public bool ReadIdent { get; init; } = true;
    public bool ReadFaults { get; init; } = true;

    /// <summary>Restrict the scan to these ECU keys (EcuDefinition.Key). Null = all.</summary>
    public IReadOnlySet<string>? OnlyKeys { get; init; }

    /// <summary>Ask the car for its ECU list first (IDENT_FUNKTIONAL). Falls back to probing every slot.</summary>
    public bool UseDiscovery { get; init; } = true;
}

/// <summary>INPA/ISTA style quick test: identify every ECU of the chassis and read its fault memory.</summary>
public sealed class QuickTestService
{
    private readonly IDiagnosticEngine _engine;

    public QuickTestService(IDiagnosticEngine engine) => _engine = engine;

    public async Task<QuickTestReport> RunAsync(
        SeriesDefinition series,
        VehicleIdentity vehicle,
        QuickTestOptions options,
        IProgress<EcuScanResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.Now;
        var results = new List<EcuScanResult>();
        var cancelled = false;

        // 1) Let the car report its modules (BN2000 functional ident). 2) Otherwise probe every catalogue slot.
        var known = new Dictionary<string, (string Variant, IReadOnlyList<IdentItem> Ident)>(StringComparer.OrdinalIgnoreCase);
        List<EcuDefinition> ecus;
        string note;
        IReadOnlyList<DiscoveredEcu> discovered = [];
        if (options.UseDiscovery && options.OnlyKeys is null && series.MainSgbd is { } mainSgbd)
        {
            discovered = await new ModuleDiscovery(_engine).DiscoverAsync(mainSgbd, cancellationToken).ConfigureAwait(false);
        }

        if (discovered.Count > 0)
        {
            ecus = [];
            foreach (var d in discovered)
            {
                var def = series.Ecus.FirstOrDefault(e => d.Group is not null && e.Groups.Contains(d.Group, StringComparer.OrdinalIgnoreCase))
                          ?? series.Ecus.FirstOrDefault(e => e.Address == d.Address)
                          ?? new EcuDefinition(d.Address, d.Abbreviation, [d.Group ?? d.Variant ?? $"0x{d.Address:X2}"]);
                if (ecus.Any(e => e.Key == def.Key))
                {
                    def = new EcuDefinition(d.Address, d.Abbreviation, [d.Group ?? d.Variant ?? $"0x{d.Address:X2}"]);
                }

                ecus.Add(def);
                if (d.Variant is not null)
                {
                    known[def.Key] = (d.Variant, d.Ident);
                }
            }

            note = $"{ecus.Count} modules reported by the car ({series.MainSgbd} functional ident)";
        }
        else
        {
            // Several catalogue entries can share an address (e.g. FZD/RLS on 0x56): scan each group once.
            ecus = series.Ecus
                .Where(e => options.OnlyKeys is null || options.OnlyKeys.Contains(e.Key))
                .GroupBy(e => e.PrimaryGroup, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            note = $"probed {ecus.Count} module slots of the {series.Series}";
        }

        ecus = ecus.OrderBy(e => e.Priority).ThenBy(e => e.Address).ToList();

        foreach (var ecu in ecus)
        {
            progress?.Report(new EcuScanResult(ecu, EcuScanState.Pending));
        }

        foreach (var ecu in ecus)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            progress?.Report(new EcuScanResult(ecu, EcuScanState.Scanning));
            EcuScanResult result;
            try
            {
                result = known.TryGetValue(ecu.Key, out var k)
                    ? await ScanEcuAsync(ecu, options, cancellationToken, k.Variant, k.Ident).ConfigureAwait(false)
                    : await ScanEcuAsync(ecu, options, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                result = new EcuScanResult(ecu, EcuScanState.Pending);
            }

            results.Add(result);
            progress?.Report(result);
            if (cancelled)
            {
                break;
            }
        }

        return new QuickTestReport(series.Series, vehicle, started, DateTimeOffset.Now - started, results, cancelled)
        {
            DiscoveryNote = note,
        };
    }

    public async Task<EcuScanResult> ScanEcuAsync(EcuDefinition ecu, QuickTestOptions options, CancellationToken cancellationToken,
        string? knownVariant = null, IReadOnlyList<IdentItem>? knownIdent = null)
    {
        // A variant reported by the functional ident needs no group identification.
        string? variant = knownVariant;
        DiagnosticException? lastError = null;

        foreach (var group in knownVariant is null ? ecu.Groups : [])
        {
            try
            {
                variant = await _engine.ResolveVariantAsync(group, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (DiagnosticException ex)
            {
                lastError = ex;
                if (ex.IsInterfaceError)
                {
                    throw; // cable problem: abort the whole test rather than marking every ECU missing
                }
            }
        }

        if (variant is null)
        {
            // No answer (or no matching variant) means the module is not fitted on this car.
            return new EcuScanResult(ecu, EcuScanState.NotFitted, Error: lastError?.Message);
        }

        IReadOnlyList<IdentItem>? ident = knownIdent is { Count: > 0 } ? knownIdent : null;
        IReadOnlyList<FaultEntry>? faults = null;
        string? error = null;

        if (options.ReadIdent && ident is null)
        {
            try
            {
                ident = FaultParser.ParseIdent(await _engine.ExecuteAsync(variant, "IDENT", "", cancellationToken).ConfigureAwait(false));
            }
            catch (DiagnosticException ex)
            {
                error = $"IDENT: {ex.Message}";
            }
        }

        if (options.ReadFaults)
        {
            try
            {
                var fs = await _engine.ExecuteAsync(variant, "FS_LESEN", "", cancellationToken).ConfigureAwait(false);
                faults = FaultParser.ParseFaults(fs);
                if (!fs.IsOkay && faults.Count == 0)
                {
                    error = $"FS_LESEN: {fs.JobStatus ?? "no JOB_STATUS"}";
                }
            }
            catch (DiagnosticException ex)
            {
                error = $"FS_LESEN: {ex.Message}";
            }
        }

        var state = faults is { Count: > 0 } ? EcuScanState.Faults
            : error is not null ? EcuScanState.Error
            : EcuScanState.NoFaults;
        return new EcuScanResult(ecu, state, variant, ident, faults, error);
    }

    /// <summary>Clears fault memory (FS_LOESCHEN) of every ECU that reported faults.</summary>
    public async Task<IReadOnlyList<(EcuScanResult Ecu, bool Ok, string Message)>> ClearAllAsync(
        IEnumerable<EcuScanResult> withFaults, CancellationToken cancellationToken = default)
    {
        var outcome = new List<(EcuScanResult, bool, string)>();
        foreach (var ecu in withFaults.Where(e => e.Variant is not null && e.FaultList.Count > 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var r = await _engine.ExecuteAsync(ecu.Variant!, "FS_LOESCHEN", "", cancellationToken).ConfigureAwait(false);
                outcome.Add((ecu, r.IsOkay, r.JobStatus ?? "no JOB_STATUS"));
            }
            catch (DiagnosticException ex)
            {
                outcome.Add((ecu, false, ex.Message));
            }
        }

        return outcome;
    }
}
