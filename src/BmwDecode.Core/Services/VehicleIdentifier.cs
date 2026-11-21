using System.Text.RegularExpressions;
using BmwDecode.Core.Diagnostics;

namespace BmwDecode.Core.Services;

/// <summary>Battery voltage and ignition as reported by the diagnostic interface.</summary>
public sealed record SupplyStatus(long? BatteryMillivolts, bool? IgnitionOn)
{
    public string BatteryText => BatteryMillivolts is { } mv ? $"{mv / 1000.0:0.0} V" : "n/a";
    public string IgnitionText => IgnitionOn switch { true => "On", false => "Off", _ => "n/a" };
}

/// <summary>What the car told us about itself.</summary>
public sealed record VehicleIdentity(
    string? Vin,
    string? VinSource,
    string? Series,
    string? SeriesSource,
    string? VehicleOrder,
    long? BatteryMillivolts,
    bool? IgnitionOn)
{
    public static VehicleIdentity Unknown { get; } = new(null, null, null, null, null, null, null);

    /// <summary>Last 7 characters of the VIN, as BMW uses on documents and in ISTA.</summary>
    public string? ShortVin => Vin is { Length: 17 } ? Vin[^7..] : Vin;
}

/// <summary>Reads VIN and vehicle order (FA) from an E-series car using the BN2000 jobs ISTA uses.</summary>
public sealed partial class VehicleIdentifier
{
    // Order follows EdiabasLib's DetectVehicleBmw (BN2000): CAS first, then light/footwell module, gateway.
    private static readonly (string Sgbd, string Job, string Result)[] s_vinJobs =
    [
        ("D_CAS", "STATUS_FAHRGESTELLNUMMER", "FGNUMMER"),
        ("D_CAS", "C_FG_LESEN", "FG_NR"),
        ("D_LM", "READ_FVIN", "FVIN"),
        ("FRM_87", "READ_FVIN", "FVIN"),
        ("D_ZGM", "C_FG_LESEN", "FG_NR"),
        ("D_KOMBI", "C_FG_LESEN", "FG_NR"),
    ];

    private static readonly (string Sgbd, string Job, string Result)[] s_faJobs =
    [
        ("D_CAS", "C_FA_LESEN", "FAHRZEUGAUFTRAG"),
        ("D_LM", "C_FA_LESEN", "FAHRZEUGAUFTRAG"),
        ("D_KBM", "C_FA_LESEN", "FAHRZEUGAUFTRAG"),
    ];

    private readonly IDiagnosticEngine _engine;

    public VehicleIdentifier(IDiagnosticEngine engine) => _engine = engine;

    public async Task<VehicleIdentity> IdentifyAsync(CancellationToken cancellationToken = default)
    {
        string? vin = null, vinSource = null, fa = null, series = null, seriesSource = null;
        var supply = await ReadSupplyAsync(cancellationToken).ConfigureAwait(false);
        long? battery = supply.BatteryMillivolts;
        bool? ignition = supply.IgnitionOn;

        foreach (var (sgbd, job, result) in s_vinJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var r = await TryRunAsync(sgbd, job, cancellationToken).ConfigureAwait(false);
            if (r is null)
            {
                continue;
            }

            ignition ??= r.IgnitionOn;
            var candidate = NormalizeVin(r.Sets.Select(s => s.Text(result)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)));
            if (candidate is not null)
            {
                vin = candidate;
                vinSource = $"{r.Variant} {job}";
                break;
            }
        }

        foreach (var (sgbd, job, result) in s_faJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var r = await TryRunAsync(sgbd, job, cancellationToken).ConfigureAwait(false);
            var candidate = r?.Sets.Select(s => s.Text(result)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (candidate is null)
            {
                continue;
            }

            fa = candidate;
            series = await DecodeSeriesAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (series is not null)
            {
                seriesSource = $"vehicle order from {r!.Variant}";
            }

            break;
        }

        return new VehicleIdentity(vin, vinSource, series, seriesSource, fa, battery, ignition);
    }

    /// <summary>
    /// Reads battery voltage and ignition from the interface via UTILITY.PRG (as INPA's status bar does).
    /// K+DCAN cables cannot measure voltage precisely; values are what the interface reports.
    /// </summary>
    public async Task<SupplyStatus> ReadSupplyAsync(CancellationToken cancellationToken = default)
    {
        long? millivolts = null;
        bool? ignition = null;

        var ubatt = await TryRunAsync("UTILITY", "STATUS_UBATT", cancellationToken).ConfigureAwait(false);
        if (ubatt?.Sets.Select(s => s.Long("STAT_UBATT")).FirstOrDefault(v => v is not null) is { } mv and > 0)
        {
            millivolts = mv;
        }

        var kl15 = await TryRunAsync("UTILITY", "STATUS_ZUENDUNG", cancellationToken).ConfigureAwait(false);
        if (kl15?.Sets.Select(s => s.Long("STAT_ZUENDUNG")).FirstOrDefault(v => v is not null) is { } on)
        {
            ignition = on > 0;
        }

        ignition ??= ubatt?.IgnitionOn ?? kl15?.IgnitionOn;
        return new SupplyStatus(millivolts, ignition);
    }

    /// <summary>
    /// Decodes the series (Baureihe) from an FA stream. Uses FA.PRG's FA_STREAM2STRUCT (pure computation, as ISTA)
    /// and falls back to the leading "E84_" token of the stream.
    /// </summary>
    public async Task<string?> DecodeSeriesAsync(string vehicleOrder, CancellationToken cancellationToken = default)
    {
        var r = await TryRunAsync("FA", "FA_STREAM2STRUCT", cancellationToken, "1;" + vehicleOrder).ConfigureAwait(false);
        var br = r?.Sets.Select(s => s.Text("BR")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return NormalizeSeries(br) ?? SeriesFromOrder(vehicleOrder);
    }

    internal static string? SeriesFromOrder(string? vehicleOrder)
    {
        if (string.IsNullOrWhiteSpace(vehicleOrder))
        {
            return null;
        }

        var m = OrderSeriesRegex().Match(vehicleOrder.Trim());
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    internal static string? NormalizeSeries(string? br)
    {
        if (string.IsNullOrWhiteSpace(br))
        {
            return null;
        }

        var m = SeriesRegex().Match(br.Trim());
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }

    /// <summary>Returns a clean 17-character VIN (or the 7-character short VIN) or null for garbage.</summary>
    public static string? NormalizeVin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var vin = raw.Trim().ToUpperInvariant();
        return VinRegex().IsMatch(vin) && !vin.All(c => c == '0') ? vin : null;
    }

    private async Task<JobResult?> TryRunAsync(string sgbd, string job, CancellationToken ct, string args = "")
    {
        try
        {
            var r = await _engine.ExecuteAsync(sgbd, job, args, ct).ConfigureAwait(false);
            return r.IsOkay ? r : null;
        }
        catch (DiagnosticException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^([EFGIRK]\d{2,3})[_#]", RegexOptions.IgnoreCase)]
    private static partial Regex OrderSeriesRegex();

    [GeneratedRegex(@"^[EFGIRK]\d{2,3}", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesRegex();

    [GeneratedRegex(@"^([A-HJ-NPR-Z0-9]{17}|[A-HJ-NPR-Z0-9]{7})$")]
    private static partial Regex VinRegex();
}
