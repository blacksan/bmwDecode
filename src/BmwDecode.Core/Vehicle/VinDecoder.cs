using System.Text.RegularExpressions;

namespace BmwDecode.Core.Vehicle;

/// <summary>Structural decode of a BMW VIN. Needs no database and works for full and 7-character short VINs.</summary>
public sealed record VinDecode
{
    public required string Input { get; init; }

    /// <summary>Full 17-character VIN, if one was given.</summary>
    public string? Vin { get; init; }

    /// <summary>Last 7 characters: what BMW, RealOEM and ETK use to identify a car.</summary>
    public required string ShortVin { get; init; }

    public bool IsValid => Problems.Count == 0;
    public IReadOnlyList<string> Problems { get; init; } = [];

    public string? Wmi { get; init; }
    public string? Manufacturer { get; init; }
    public string? Country { get; init; }

    /// <summary>BMW type key (VIN positions 4–7), e.g. VN31. Identifies model, engine, body and market.</summary>
    public string? TypeKey { get; init; }

    public char? CheckDigit { get; init; }

    /// <summary>True/false for VINs that use the North American check digit; null when not applicable.</summary>
    public bool? CheckDigitValid { get; init; }

    /// <summary>Model year from position 10 (North American VINs only). Null for European VINs.</summary>
    public int? ModelYear { get; init; }

    public string? ModelYearNote { get; init; }
    public char? PlantCode { get; init; }
    public string? Plant { get; init; }
    public string? Serial { get; init; }

    /// <summary>Uses the North American VIN layout (check digit and model year are meaningful).</summary>
    public bool IsNorthAmericanFormat { get; init; }
}

public static partial class VinDecoder
{
    private static readonly int[] Weights = [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2];

    private static readonly Dictionary<string, (string Maker, string Country)> s_wmi = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WBA"] = ("BMW AG – passenger car", "Germany"),
        ["WBS"] = ("BMW M GmbH", "Germany"),
        ["WBX"] = ("BMW AG – SAV (X models)", "Germany / Austria"),
        ["WBY"] = ("BMW i", "Germany"),
        ["WBW"] = ("BMW AG", "Germany"),
        ["WB1"] = ("BMW Motorrad", "Germany"),
        ["WB3"] = ("BMW Motorrad", "Germany"),
        ["4US"] = ("BMW Manufacturing Co. (Spartanburg)", "USA"),
        ["5UX"] = ("BMW Manufacturing Co. – SAV (Spartanburg)", "USA"),
        ["5UM"] = ("BMW Manufacturing Co. – M SAV (Spartanburg)", "USA"),
        ["5YM"] = ("BMW Manufacturing Co. – M SAV (Spartanburg)", "USA"),
        ["3MW"] = ("BMW de México (San Luis Potosí)", "Mexico"),
        ["3MF"] = ("BMW M – Mexico", "Mexico"),
        ["LBV"] = ("BMW Brilliance Automotive", "China"),
        ["X4X"] = ("Avtotor (BMW assembly)", "Russia"),
        ["WMW"] = ("MINI", "United Kingdom / Germany"),
        ["SCA"] = ("Rolls-Royce Motor Cars", "United Kingdom"),
    };

    // VIN position 11 / first character of the short VIN. Compiled from public BMW VIN references.
    private static readonly Dictionary<char, string> s_plants = new()
    {
        ['A'] = "Munich, Germany",
        ['F'] = "Munich, Germany",
        ['K'] = "Munich, Germany",
        ['8'] = "Munich, Germany (older) / San Luis Potosí, Mexico (2019+)",
        ['B'] = "Dingolfing, Germany",
        ['C'] = "Dingolfing, Germany",
        ['D'] = "Dingolfing, Germany",
        ['G'] = "Dingolfing, Germany",
        ['E'] = "Regensburg, Germany",
        ['J'] = "Regensburg, Germany",
        ['P'] = "Regensburg, Germany",
        ['V'] = "Leipzig, Germany",
        ['L'] = "Spartanburg, USA",
        ['M'] = "Spartanburg, USA",
        ['N'] = "Rosslyn, South Africa",
        ['H'] = "Rosslyn, South Africa",
        ['W'] = "Graz (Magna Steyr), Austria",
        ['X'] = "Kaliningrad (Avtotor), Russia / CKD assembly",
    };

    /// <summary>Decodes a full 17-character VIN or a 7-character short VIN. Spaces and dashes are ignored.</summary>
    public static VinDecode Decode(string? input, int? productionYearHint = null)
    {
        var raw = input ?? string.Empty;
        var vin = CleanRegex().Replace(raw, string.Empty).ToUpperInvariant();
        var problems = new List<string>();

        if (vin.Length == 7)
        {
            if (!ShortRegex().IsMatch(vin))
            {
                problems.Add("A short VIN has 7 characters: letters and digits except I, O and Q.");
            }

            return new VinDecode
            {
                Input = raw,
                ShortVin = vin,
                PlantCode = vin[0],
                Plant = s_plants.GetValueOrDefault(vin[0]),
                Serial = vin[1..],
                Problems = problems,
            };
        }

        if (vin.Length != 17)
        {
            problems.Add($"A VIN has 17 characters (or 7 for the short VIN); this has {vin.Length}.");
            return new VinDecode { Input = raw, ShortVin = vin.Length >= 7 ? vin[^7..] : vin, Problems = problems };
        }

        if (!FullRegex().IsMatch(vin))
        {
            problems.Add("VINs use letters and digits only, never I, O or Q.");
        }

        var wmi = vin[..3];
        var known = s_wmi.TryGetValue(wmi, out var maker);
        if (!known)
        {
            problems.Add($"{wmi} is not a BMW Group manufacturer code.");
        }

        var check = vin[8];
        var computed = ComputeCheckDigit(vin);
        // North American VINs (US-built, or cars built for US/Canada) carry a valid check digit and model year.
        // European VINs often pass the check by chance (filler zeros), so position 10 must also be a model-year code.
        var northAmerican = computed is not null && computed == check && YearCodes.Contains(vin[9], StringComparison.Ordinal);

        int? modelYear = null;
        string? yearNote;
        if (northAmerican)
        {
            (modelYear, yearNote) = ModelYear(vin[9], productionYearHint);
        }
        else
        {
            yearNote = "Not encoded – European/RoW VINs use position 10 as a filler. The production date comes from the car or the parts catalogue.";
        }

        return new VinDecode
        {
            Input = raw,
            Vin = vin,
            ShortVin = vin[^7..],
            Wmi = wmi,
            Manufacturer = known ? maker.Maker : null,
            Country = known ? maker.Country : null,
            TypeKey = vin[3..7],
            CheckDigit = check,
            CheckDigitValid = northAmerican ? true : null,
            IsNorthAmericanFormat = northAmerican,
            ModelYear = modelYear,
            ModelYearNote = yearNote,
            PlantCode = vin[10],
            Plant = s_plants.GetValueOrDefault(vin[10]),
            Serial = vin[11..],
            Problems = problems,
        };
    }

    /// <summary>ISO 3779 / 49 CFR 565 check digit, or null if the VIN contains invalid characters.</summary>
    public static char? ComputeCheckDigit(string vin)
    {
        if (vin.Length != 17)
        {
            return null;
        }

        var sum = 0;
        for (var i = 0; i < 17; i++)
        {
            var c = char.ToUpperInvariant(vin[i]);
            int value;
            if (c is >= '0' and <= '9')
            {
                value = c - '0';
            }
            else if (c is >= 'A' and <= 'Z' and not ('I' or 'O' or 'Q'))
            {
                value = TransliterationValue(c);
            }
            else
            {
                return null;
            }

            sum += value * Weights[i];
        }

        var r = sum % 11;
        return r == 10 ? 'X' : (char)('0' + r);
    }

    private static int TransliterationValue(char c) => c switch
    {
        'A' or 'J' => 1,
        'B' or 'K' or 'S' => 2,
        'C' or 'L' or 'T' => 3,
        'D' or 'M' or 'U' => 4,
        'E' or 'N' or 'V' => 5,
        'F' or 'W' => 6,
        'G' or 'P' or 'X' => 7,
        'H' or 'Y' => 8,
        'R' or 'Z' => 9,
        _ => 0,
    };

    private const string YearCodes = "ABCDEFGHJKLMNPRSTVWXY123456789";

    private static (int? Year, string Note) ModelYear(char code, int? hint)
    {
        var index = YearCodes.IndexOf(code, StringComparison.Ordinal);
        if (index < 0)
        {
            return (null, $"'{code}' is not a model-year code.");
        }

        var early = 1980 + index;
        var late = early + 30;
        var latestPossible = DateTime.Now.Year + 1;
        if (hint is { } h)
        {
            var pick = Math.Abs(h - early) <= Math.Abs(h - late) ? early : late;
            return (pick, $"Model year {pick}");
        }

        return late <= latestPossible
            ? (late, $"Model year {late} (or {early} – the code repeats every 30 years)")
            : (early, $"Model year {early}");
    }

    [GeneratedRegex(@"[\s\-_.]")]
    private static partial Regex CleanRegex();

    [GeneratedRegex(@"^[A-HJ-NPR-Z0-9]{7}$")]
    private static partial Regex ShortRegex();

    [GeneratedRegex(@"^[A-HJ-NPR-Z0-9]{17}$")]
    private static partial Regex FullRegex();
}
