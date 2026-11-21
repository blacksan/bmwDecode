using System.Globalization;
using System.Text.RegularExpressions;
using BmwDecode.Core.Catalog;
using BmwDecode.Core.Diagnostics;
using BmwDecode.Core.Vehicle;

namespace BmwDecode.Core.Services;

/// <summary>Battery voltage and ignition as reported by the diagnostic interface.</summary>
public sealed record SupplyStatus(long? BatteryMillivolts, bool? IgnitionOn)
{
    public string BatteryText => BatteryMillivolts is { } mv ? $"{mv / 1000.0:0.0} V" : "n/a";
    public string IgnitionText => IgnitionOn switch { true => "On", false => "Off", _ => "n/a" };
}

/// <summary>RealOEM-style description of a car, assembled from everything the car (and the SGBD files) can tell us.</summary>
public sealed record VehicleDetails
{
    public static VehicleDetails Empty { get; } = new();

    public string? TypeKey { get; init; }
    public string? Model { get; init; }
    public string? Engine { get; init; }
    public string? EngineEcu { get; init; }
    public string? Drive { get; init; }
    public string? Gearbox { get; init; }
    public string? Displacement { get; init; }
    public string? Body { get; init; }
    public string? Power { get; init; }
    public string? Steering { get; init; }
    public string? Doors { get; init; }
    public string? Market { get; init; }
    public DateOnly? ProductionDate { get; init; }
    public string? Paint { get; init; }
    public string? Upholstery { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>Where each piece of information came from, for the "evidence" list.</summary>
    public IReadOnlyList<string> Sources { get; init; } = [];

    public string? ProductionText => ProductionDate?.ToString("MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>Label/value pairs for display, skipping unknown values.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Rows()
    {
        var rows = new List<KeyValuePair<string, string>>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                rows.Add(new(label, value.Trim()));
            }
        }

        Add("Type key", TypeKey);
        Add("Model", Model);
        Add("Engine", Engine);
        Add("Engine ECU", EngineEcu);
        Add("Displacement", Displacement);
        Add("Power", Power);
        Add("Gearbox", Gearbox);
        Add("Drive", Drive);
        Add("Body", Body);
        Add("Doors", Doors);
        Add("Steering", Steering);
        Add("Market", Market);
        Add("Production", ProductionText);
        Add("Paint code", Paint);
        Add("Upholstery code", Upholstery);
        return rows;
    }
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

    public VehicleDetails Details { get; init; } = VehicleDetails.Empty;

    public VehicleOrder? Order { get; init; }

    /// <summary>Last 7 characters of the VIN, as BMW uses on documents, in ETK and RealOEM.</summary>
    public string? ShortVin => Vin is { Length: 17 } ? Vin[^7..] : Vin;

    public VinDecode? VinInfo => Vin is null ? null : VinDecoder.Decode(Vin, Details.ProductionDate?.Year);
}

/// <summary>
/// Identifies E-series cars of both generations: BN2000 / D-CAN cars (E6x/E7x/E8x/E9x) via CAS, FA and FA.PRG,
/// and DS2 / K-line cars (E36/E38/E39/E46/E52/E53/E83/E85) via the ZCS coding key and FZGIDENT.
/// </summary>
public sealed partial class VehicleIdentifier
{
    // BN2000 (EdiabasLib DetectVehicleBmw order): CAS first, then light/footwell module, gateway.
    private static readonly (string Sgbd, string Job, string Result)[] s_vinJobsBn2000 =
    [
        ("D_CAS", "STATUS_FAHRGESTELLNUMMER", "FGNUMMER"),
        ("D_CAS", "C_FG_LESEN", "FG_NR"),
        ("D_LM", "READ_FVIN", "FVIN"),
        ("FRM_87", "READ_FVIN", "FVIN"),
        ("D_ZGM", "C_FG_LESEN", "FG_NR"),
        ("D_KOMBI", "C_FG_LESEN", "FG_NR"),
    ];

    // DS2 (ISTA ReadVinForGroupCars): coding key, then light check module / engine / immobiliser.
    private static readonly (string Sgbd, string Job, string Result)[] s_vinJobsDs2 =
    [
        ("ZCS_ALL", "FGNR_LESEN", "FG_NR"),
        ("D_0080", "AIF_FG_NR_LESEN", "AIF_FG_NR"),
        ("D_0010", "AIF_LESEN", "AIF_FG_NR"),
        ("EWS3", "FGNR_LESEN", "FG_NR"),
    ];

    private static readonly (string Sgbd, string Job, string Result)[] s_faJobs =
    [
        ("D_CAS", "C_FA_LESEN", "FAHRZEUGAUFTRAG"),
        ("D_LM", "C_FA_LESEN", "FAHRZEUGAUFTRAG"),
        ("D_KBM", "C_FA_LESEN", "FAHRZEUGAUFTRAG"),
        ("D_0080", "C_FA_LESEN", "FAHRZEUGAUFTRAG"),
        ("D_00D0", "C_FA_LESEN", "FAHRZEUGAUFTRAG"),
    ];

    private readonly IDiagnosticEngine _engine;
    private readonly TypeKeyDatabase _typeKeys;

    public VehicleIdentifier(IDiagnosticEngine engine, TypeKeyDatabase? typeKeys = null)
    {
        _engine = engine;
        _typeKeys = typeKeys ?? TypeKeyDatabase.Empty;
    }

    /// <param name="profile">The chassis profile currently selected; decides which generation's jobs are tried first.</param>
    public async Task<VehicleIdentity> IdentifyAsync(SeriesDefinition? profile = null, CancellationToken cancellationToken = default)
    {
        var supply = await ReadSupplyAsync(cancellationToken).ConfigureAwait(false);
        var ds2First = profile?.IsDs2 == true;
        var sources = new List<string>();

        // ---- VIN ----
        string? vin = null, vinSource = null;
        var vinJobs = ds2First ? s_vinJobsDs2.Concat(s_vinJobsBn2000) : s_vinJobsBn2000.Concat(s_vinJobsDs2);
        if (profile?.IdentSgbd is { } zcs && !zcs.Equals("ZCS_ALL", StringComparison.OrdinalIgnoreCase))
        {
            vinJobs = vinJobs.Prepend((zcs, "FGNR_LESEN", "FG_NR"));
        }

        foreach (var (sgbd, job, result) in vinJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var r = await TryRunAsync(sgbd, job, cancellationToken).ConfigureAwait(false);
            if (NormalizeVin(r?.Sets.Select(s => s.Text(result)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))) is { } candidate)
            {
                vin = candidate;
                vinSource = $"{r!.Variant} {job}";
                break;
            }
        }

        // ---- Vehicle order (FA) ----
        string? faRaw = null, series = null, seriesSource = null;
        VehicleOrder? order = null;
        foreach (var (sgbd, job, result) in s_faJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var r = await TryRunAsync(sgbd, job, cancellationToken).ConfigureAwait(false);
            var candidate = r?.Sets.Select(s => s.Text(result)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (candidate is null)
            {
                continue;
            }

            faRaw = candidate;
            var (standardFa, br) = await DecodeFaAsync(candidate, cancellationToken).ConfigureAwait(false);
            order = VehicleOrderParser.Parse(standardFa ?? candidate);
            series = NormalizeSeries(br) ?? order?.Series;
            if (series is not null)
            {
                seriesSource = $"vehicle order from {r!.Variant}";
            }

            sources.Add($"Vehicle order (FA) from {r!.Variant}");
            break;
        }

        var details = new VehicleDetails
        {
            TypeKey = order?.TypeKey ?? (vin is { Length: 17 } ? vin[3..7] : null),
            ProductionDate = order?.ProductionDate,
            Paint = order?.Paint,
            Upholstery = order?.Upholstery,
            Options = order?.Options ?? [],
        };

        // ---- DS2 coding key (ZCS): series, model, engine, gearbox... straight from the car ----
        if (order is null)
        {
            foreach (var zcsSgbd in new[] { profile?.IdentSgbd, "ZCS_ALL" }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var gm = await TryRunAsync(zcsSgbd, "ZCS_GM_LESEN", cancellationToken).ConfigureAwait(false);
                if (gm?.Sets.FirstOrDefault(s => s.ContainsKey("BR_TXT")) is not { } set)
                {
                    continue;
                }

                details = Merge(details, set);
                series ??= NormalizeSeries(set.Text("BR_TXT"));
                seriesSource ??= series is null ? null : $"coding key from {gm.Variant}";
                sources.Add($"Coding key (ZCS) from {gm.Variant}");

                var prod = await TryRunAsync(zcsSgbd, "PROD_DATUM_LESEN", cancellationToken).ConfigureAwait(false);
                if (prod?.Sets.FirstOrDefault() is { } p && p.Long("JAHR_DEC") is { } y and > 1980 && p.Long("MONAT_DEC") is { } mo and >= 1 and <= 12)
                {
                    details = details with { ProductionDate = new DateOnly((int)y, (int)mo, 1) };
                }

                break;
            }
        }

        // ---- Type key: local database first (FZGIDENT tables + the user's typekeys.csv), then FZGIDENT jobs ----
        if (_typeKeys.Find(details.TypeKey) is { } known)
        {
            details = Merge(details, known);
            series ??= NormalizeSeries(known.Series);
            sources.Add($"Type key {known.TypeKey} from {known.Source}");
        }

        if (details.TypeKey is { } typeKey && details.Model is null)
        {
            var gm = await TryRunAsync("FZGIDENT", "GM_LESEN_TYPSCHLUESSEL", cancellationToken, typeKey).ConfigureAwait(false);
            if (gm?.Sets.FirstOrDefault(s => !string.IsNullOrWhiteSpace(Clean(s.Text("VERK_TXT"))) || !string.IsNullOrWhiteSpace(Clean(s.Text("BR_TXT")))) is { } set)
            {
                details = Merge(details, set);
                series ??= NormalizeSeries(set.Text("BR_TXT"));
                sources.Add("Type key table in FZGIDENT.PRG");
            }
            else
            {
                var str = await TryRunAsync("FZGIDENT", "STRINGS_LESEN_TYPSCHLUESSEL", cancellationToken, typeKey).ConfigureAwait(false);
                if (str?.Sets.FirstOrDefault(s => !string.IsNullOrWhiteSpace(Clean(s.Text("MO_TXT")))) is { } s2)
                {
                    details = details with { Model = details.Model ?? Clean(s2.Text("MO_TXT")), Market = details.Market ?? Clean(s2.Text("LA_TXT")) };
                    series ??= NormalizeSeries(s2.Text("BR_TXT"));
                    sources.Add("Type key strings in FZGIDENT.PRG");
                }
            }
        }

        // ---- Engine ECU: the fitted DME/DDE variant names the engine (and reveals swaps) ----
        var engineGroups = ds2First ? new[] { "D_0012", "D_MOTOR" } : new[] { "D_MOTOR", "D_0012" };
        foreach (var group in engineGroups)
        {
            try
            {
                var variant = await _engine.ResolveVariantAsync(group, cancellationToken).ConfigureAwait(false);
                details = details with
                {
                    EngineEcu = variant,
                    Engine = details.Engine ?? EngineHints.Describe(variant),
                };
                sources.Add($"Engine ECU identified as {variant}");
                break;
            }
            catch (DiagnosticException)
            {
                // Try the other generation's group file.
            }
        }

        if (vinSource is not null)
        {
            sources.Insert(0, $"VIN from {vinSource}");
        }

        details = details with { Sources = sources };
        return new VehicleIdentity(vin, vinSource, series, seriesSource, faRaw, supply.BatteryMillivolts, supply.IgnitionOn)
        {
            Details = details,
            Order = order,
        };
    }

    /// <summary>
    /// Reads battery voltage and ignition via UTILITY.PRG (as INPA's status bar does).
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
    /// Runs FA.PRG's FA_STREAM2STRUCT (pure computation, as ISTA does) on the raw stream.
    /// Returns the standard FA and series if the SGBD accepted the stream.
    /// </summary>
    public async Task<(string? StandardFa, string? Series)> DecodeFaAsync(string vehicleOrder, CancellationToken cancellationToken = default)
    {
        var r = await TryRunAsync("FA", "FA_STREAM2STRUCT", cancellationToken, "1;" + vehicleOrder).ConfigureAwait(false);
        var std = r?.Sets.Select(s => s.Text("STANDARD_FA")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var br = r?.Sets.Select(s => s.Text("BR")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return (std, br);
    }

    internal static string? SeriesFromOrder(string? vehicleOrder) => VehicleOrderParser.Parse(vehicleOrder)?.Series;

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

    /// <summary>Fills details from a ZCS_GM_LESEN / GM_LESEN_TYPSCHLUESSEL result set (TYPSNR_TXT, VERK_TXT, MOTOR_TXT...).</summary>
    internal static VehicleDetails Merge(VehicleDetails d, ResultSet s) => d with
    {
        TypeKey = d.TypeKey ?? Clean(s.Text("TYPSNR_TXT")),
        Model = d.Model ?? Clean(s.Text("VERK_TXT")),
        Engine = d.Engine ?? Clean(s.Text("MOTOR_TXT")),
        Drive = d.Drive ?? Clean(s.Text("ANTR_TXT")),
        Gearbox = d.Gearbox ?? Clean(s.Text("GETR_TXT")),
        Displacement = d.Displacement ?? Clean(s.Text("HUBR_TXT")),
        Body = d.Body ?? Clean(s.Text("KAROS_TXT")),
        Power = d.Power ?? (Clean(s.Text("LEIST_TXT")) is { } kw ? (kw.Any(char.IsLetter) ? kw : kw + " kW") : null),
        Steering = d.Steering ?? Clean(s.Text("LENK_TXT")),
        Doors = d.Doors ?? Clean(s.Text("TUER_TXT")),
        Market = d.Market ?? Clean(s.Text("LAND_TXT")),
    };

    /// <summary>Fills details from a type key database entry.</summary>
    public static VehicleDetails Merge(VehicleDetails d, TypeKeyInfo t) => d with
    {
        TypeKey = d.TypeKey ?? t.TypeKey,
        Model = d.Model ?? t.Model,
        Engine = d.Engine ?? t.Engine,
        Drive = d.Drive ?? t.Drive,
        Gearbox = d.Gearbox ?? t.Gearbox,
        Displacement = d.Displacement ?? t.Displacement,
        Body = d.Body ?? t.Body,
        Power = d.Power ?? t.Power,
        Steering = d.Steering ?? t.Steering,
        Doors = d.Doors ?? t.Doors,
        Market = d.Market ?? t.Market,
    };

    private static string? Clean(string? s) =>
        string.IsNullOrWhiteSpace(s) || s.Trim() is "-" or "--" or "?" or "XX" ? null : s.Trim();

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

    [GeneratedRegex(@"^[EFGIRK]\d{2,3}", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesRegex();

    [GeneratedRegex(@"^([A-HJ-NPR-Z0-9]{17}|[A-HJ-NPR-Z0-9]{7})$")]
    private static partial Regex VinRegex();
}
