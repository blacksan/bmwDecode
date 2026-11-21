using System.Globalization;
using System.Text.RegularExpressions;

namespace BmwDecode.Core.Vehicle;

/// <summary>
/// Decoded BMW vehicle order (Fahrzeugauftrag, "FA") as stored in the CAS / LM / KOMBI.
/// Standard FA layout: <c>E84_#0909*VN31%0475&amp;LCSW$205$248-A8B+HO01</c> — series, #production MMYY,
/// *type key, %paint, &amp;upholstery, $option (SA) codes, -E-words, +HO-words.
/// </summary>
public sealed record VehicleOrder
{
    public required string Raw { get; init; }
    public string? Series { get; init; }
    public string? TypeKey { get; init; }
    public DateOnly? ProductionDate { get; init; }
    public string? Paint { get; init; }
    public string? Upholstery { get; init; }
    public IReadOnlyList<string> Options { get; init; } = [];
    public IReadOnlyList<string> EWords { get; init; } = [];
    public IReadOnlyList<string> HoWords { get; init; } = [];

    public string? ProductionText => ProductionDate?.ToString("MM/yyyy", CultureInfo.InvariantCulture);
}

public static partial class VehicleOrderParser
{
    public static VehicleOrder? Parse(string? fa)
    {
        if (string.IsNullOrWhiteSpace(fa))
        {
            return null;
        }

        var s = fa.Trim();

        // The raw CAS stream may carry a version prefix before the series; the series is the first E/F/R/K token.
        var series = SeriesRegex().Match(s) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : null;

        DateOnly? date = null;
        if (DateRegex().Match(s) is { Success: true } d &&
            DateTime.TryParseExact(d.Groups[1].Value, "MMyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            date = new DateOnly(dt.Year, dt.Month, 1);
        }

        return new VehicleOrder
        {
            Raw = s,
            Series = series,
            ProductionDate = date,
            TypeKey = Single(TypeRegex(), s),
            Paint = Single(PaintRegex(), s),
            Upholstery = Single(UpholsteryRegex(), s),
            Options = Many(OptionRegex(), s),
            EWords = Many(EWordRegex(), s),
            HoWords = Many(HoWordRegex(), s),
        };
    }

    private static string? Single(Regex regex, string s) =>
        regex.Match(s) is { Success: true } m ? m.Groups[1].Value.ToUpperInvariant() : null;

    private static IReadOnlyList<string> Many(Regex regex, string s) =>
        regex.Matches(s).Select(m => m.Groups[1].Value.ToUpperInvariant()).Distinct().ToList();

    [GeneratedRegex(@"(?:^|[^A-Z0-9])([EFGIRK]\d{2,3})_", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesRegex();

    [GeneratedRegex(@"#(\d{4})")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"\*([A-Z0-9]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex TypeRegex();

    [GeneratedRegex(@"%([A-Z0-9]{3,4})", RegexOptions.IgnoreCase)]
    private static partial Regex PaintRegex();

    [GeneratedRegex(@"&([A-Z0-9]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UpholsteryRegex();

    [GeneratedRegex(@"\$([A-Z0-9]{3,4})", RegexOptions.IgnoreCase)]
    private static partial Regex OptionRegex();

    [GeneratedRegex(@"-([A-Z0-9]{3,4})", RegexOptions.IgnoreCase)]
    private static partial Regex EWordRegex();

    [GeneratedRegex(@"\+([A-Z0-9]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex HoWordRegex();
}

/// <summary>Best-effort engine family from the engine SGBD name (D60M57A0 → M57 diesel).</summary>
public static partial class EngineHints
{
    private static readonly (string Prefix, string Engine)[] s_dmeFamilies =
    [
        ("MSS54", "S54 (M3 E46)"),
        ("MSS60", "S65 V8 (M3 E9x)"),
        ("MSS65", "S85 V10 (M5/M6)"),
        ("MSS70", "S85 V10 (M5/M6)"),
        ("MS41", "M52 straight-six"),
        ("MS42", "M52TU straight-six"),
        ("MS43", "M54 straight-six"),
        ("MS45", "M54 straight-six"),
        ("ME9", "N42/N45/N46 four-cylinder"),
        ("MSV70", "N52 straight-six"),
        ("MSV80", "N52KP straight-six"),
        ("MSV90", "N52 straight-six"),
        ("MSD80", "N53/N54 straight-six"),
        ("MSD81", "N53/N54 straight-six"),
        ("MSD85", "N63 V8"),
        ("MSD87", "N54 straight-six"),
        ("MEVD172", "N55 / N20 / N13"),
        ("MEVD17", "N55 / N20"),
        ("ME72", "M62 V8"),
        ("DME528", "M62 V8"),
        ("ME9N62", "N62 V8"),
        ("DME5212", "M73 V12"),
        ("DME338", "M60 V8"),
        ("DDE30", "M57 / M47 diesel (DDE 3)"),
        ("DDE40", "M57 diesel (DDE 4)"),
        ("DDE41", "M57 diesel (DDE 4.1)"),
    ];

    public static string? Describe(string? variant)
    {
        if (string.IsNullOrWhiteSpace(variant))
        {
            return null;
        }

        var v = variant.ToUpperInvariant();

        // Diesel SGBDs encode generation and engine: D60M57A0 = DDE 6.0 for M57, D73N47A0 = DDE 7.3 for N47.
        if (DieselRegex().Match(v) is { Success: true } d)
        {
            var dde = $"{d.Groups[1].Value[0]}.{d.Groups[1].Value[1]}";
            return $"{d.Groups[2].Value} diesel (DDE {dde})";
        }

        foreach (var (prefix, engine) in s_dmeFamilies.OrderByDescending(f => f.Prefix.Length))
        {
            if (v.StartsWith(prefix, StringComparison.Ordinal))
            {
                return engine;
            }
        }

        return EngineCodeRegex().Match(v) is { Success: true } e ? e.Value : null;
    }

    [GeneratedRegex(@"^D(\d{2})([MN]\d{2})")]
    private static partial Regex DieselRegex();

    [GeneratedRegex(@"[MNS]\d{2}")]
    private static partial Regex EngineCodeRegex();
}
