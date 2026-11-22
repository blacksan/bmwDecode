using System.Globalization;
using System.Text.RegularExpressions;

namespace BmwDecode.Core.Diagnostics;

/// <summary>A stored DTC from an ECU fault memory (FS_LESEN) or info memory (IS_LESEN).</summary>
public sealed record FaultEntry
{
    /// <summary>BMW fault location number (F_ORT_NR), shown in hex like INPA/ISTA (e.g. 4A2E).</summary>
    public required long Code { get; init; }

    /// <summary>Fault description, translated to English when translation is on.</summary>
    public required string Text { get; init; }

    /// <summary>Description exactly as the SGBD has it (usually German).</summary>
    public string? OriginalText { get; init; }
    public string? Symptom { get; init; }
    public string? PresentText { get; init; }
    public string? WarningText { get; init; }

    /// <summary>F_VORHANDEN_NR fault type code (0x20/0x21 stored only, 0x22/0x23 currently present).</summary>
    public long? PresentCode { get; init; }

    /// <summary>F_WARNUNG_NR fault type code (0x30 no warning lamp, 0x31 warning lamp).</summary>
    public long? WarningCode { get; init; }

    /// <summary>DS2 SGBDs: F_VORHANDEN status bit (1 = present now, 0 = stored only).</summary>
    public long? PresentBit { get; init; }

    /// <summary>DS2 SGBDs: all F_ARTn_TEXT fault-type texts.</summary>
    public IReadOnlyList<string> FaultTypes { get; init; } = [];
    public string? ReadinessText { get; init; }
    public int? Frequency { get; init; }
    public string? PCode { get; init; }

    /// <summary>All raw results of this fault, for the details pane.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Raw { get; init; } = [];

    public string CodeHex => Code.ToString(Code > 0xFFFF ? "X6" : "X4", CultureInfo.InvariantCulture);

    /// <summary>True if the ECU says the fault is currently active, false if only stored, null if unknown.</summary>
    public bool? IsPresent => PresentCode switch
    {
        0x22 or 0x23 => true,
        0x20 or 0x21 => false,
        _ => PresentBit switch
        {
            1 => true,
            0 => false,
            _ => FaultParser.InterpretPresent(PresentText) ?? FaultTypes.Select(FaultParser.InterpretPresent).FirstOrDefault(v => v is not null),
        },
    };

    /// <summary>Present / warning texts for display (translated). The raw German stays in PresentText for interpretation.</summary>
    public string? PresentDisplay => Translation.Translator.Current.TranslateOrNull(PresentText);

    public string? WarningDisplay => Translation.Translator.Current.TranslateOrNull(WarningText);

    public bool? WarningLampOn => WarningCode switch
    {
        0x31 => true,
        0x30 => false,
        _ => FaultParser.InterpretWarning(WarningText),
    };
}

public sealed record EnvironmentCondition(string Name, string Value, string? Unit);

/// <summary>Freeze-frame / environmental conditions for one fault (FS_LESEN_DETAIL).</summary>
public sealed record FaultDetail(
    long Code,
    long? Mileage,
    int? Frequency,
    int? LogisticsCounter,
    IReadOnlyList<EnvironmentCondition> Conditions,
    IReadOnlyList<KeyValuePair<string, string>> Raw);

public sealed record IdentItem(string Key, string Label, string Value);

/// <summary>A live value from a STATUS_* job.</summary>
public sealed record LiveValue(string Job, string Key, string Label, string Value, string? Unit, double? Numeric);

public static partial class FaultParser
{
    private static readonly HashSet<string> s_hidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "_TEL_AUFTRAG", "_TEL_ANTWORT", "_TEL_ANTWORT2", "_RESPONSE", "_REQUEST",
    };

    /// <summary>Parses FS_LESEN / IS_LESEN output: every set carrying F_ORT_NR is one fault.</summary>
    /// <summary>Info memory jobs (IS_LESEN...) report codes from the info memory, which has its own texts.</summary>
    public static bool IsInfoMemoryJob(string? job) =>
        job is not null && (job.StartsWith("IS_", StringComparison.OrdinalIgnoreCase)
                            || job.Contains("_IS_", StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<FaultEntry> ParseFaults(JobResult result)
    {
        var faults = new List<FaultEntry>();
        foreach (var set in result.Sets)
        {
            var code = set.Long("F_ORT_NR");
            if (code is null)
            {
                continue;
            }

            var types = set.Keys
                .Where(k => ArtRegex().IsMatch(k))
                .OrderBy(k => int.Parse(ArtRegex().Match(k).Groups[1].Value, CultureInfo.InvariantCulture))
                .Select(k => Clean(set.Text(k)))
                .OfType<string>()
                .ToList();

            var tr = Translation.Translator.Current;
            var original = Clean(set.Text("F_ORT_TEXT"));
            // BMW's own English text for this ECU variant from the data pack, when translations are on.
            var official = tr.Enabled
                ? Data.ReferenceData.FaultText(result.Variant, code.Value, infoMemory: IsInfoMemoryJob(result.Job))
                : null;
            var symptom = Clean(set.Text("F_SYMPTOM_TEXT"));
            faults.Add(new FaultEntry
            {
                Code = code.Value,
                Text = official ?? (original is null ? "(no description in SGBD)" : tr.Translate(original)),
                OriginalText = original,
                Symptom = symptom is not null ? tr.Translate(symptom) : types.Count > 0 ? string.Join("; ", types.Select(tr.Translate)) : null,
                FaultTypes = types,
                PresentBit = set.ContainsKey("F_VORHANDEN_NR") ? null : set.Long("F_VORHANDEN"),
                PresentText = Clean(set.Text("F_VORHANDEN_TEXT")),
                WarningText = Clean(set.Text("F_WARNUNG_TEXT")),
                PresentCode = set.Long("F_VORHANDEN_NR"),
                WarningCode = set.Long("F_WARNUNG_NR"),
                ReadinessText = Clean(set.Text("F_READY_TEXT")),
                Frequency = (int?)set.Long("F_HFK"),
                PCode = Clean(set.Text("F_PCODE_STRING")),
                Raw = Visible(set),
            });
        }

        return faults;
    }

    /// <summary>Parses FS_LESEN_DETAIL: mileage, frequency and all F_UWn environment conditions.</summary>
    public static FaultDetail ParseDetail(long code, JobResult result)
    {
        var conditions = new List<EnvironmentCondition>();
        long? mileage = null;
        int? frequency = null;
        int? logistics = null;
        var raw = new List<KeyValuePair<string, string>>();

        foreach (var set in result.Sets)
        {
            mileage ??= set.Long("F_UW_KM");
            frequency ??= (int?)set.Long("F_HFK");
            logistics ??= (int?)set.Long("F_LZ");
            raw.AddRange(Visible(set));

            // F_UW1_TEXT / F_UW1_WERT / F_UW1_EINH ...
            var indices = set.Keys
                .Select(k => UwRegex().Match(k))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Distinct()
                .Order();

            foreach (var i in indices)
            {
                var name = Clean(set.Text($"F_UW{i}_TEXT")) is { } n ? Translation.Translator.Current.Translate(n) : $"Condition {i}";
                var value = set.TryGetValue($"F_UW{i}_WERT", out var v) ? v.Display : string.Empty;
                var unit = Clean(set.Text($"F_UW{i}_EINH"));
                conditions.Add(new EnvironmentCondition(name, value, unit is "-" ? null : unit));
            }
        }

        return new FaultDetail(code, mileage, frequency, logistics, conditions, raw);
    }

    /// <summary>
    /// Environment conditions carried inside the FS_LESEN result itself (DS2 SGBDs have no FS_LESEN_DETAIL).
    /// </summary>
    public static FaultDetail DetailFromEntry(FaultEntry fault)
    {
        var raw = fault.Raw.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var conditions = raw.Keys
            .Select(k => UwRegex().Match(k))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .Distinct()
            .Order()
            .Select(i => new EnvironmentCondition(
                Clean(raw.GetValueOrDefault($"F_UW{i}_TEXT")) is { } n ? Translation.Translator.Current.Translate(n) : $"Condition {i}",
                raw.GetValueOrDefault($"F_UW{i}_WERT") ?? string.Empty,
                Clean(raw.GetValueOrDefault($"F_UW{i}_EINH")) is { } u && u != "-" ? u : null))
            .ToList();

        long? km = long.TryParse(raw.GetValueOrDefault("F_UW_KM"), NumberStyles.Integer, CultureInfo.CurrentCulture, out var k) ? k : null;
        int? lz = int.TryParse(raw.GetValueOrDefault("F_LZ"), out var l) ? l : null;
        return new FaultDetail(fault.Code, km, fault.Frequency, lz, conditions, fault.Raw);
    }

    /// <summary>Extracts identification data from IDENT (or any *IDENT* job) with friendly labels.</summary>
    public static IReadOnlyList<IdentItem> ParseIdent(JobResult result)
    {
        var items = new List<IdentItem>();
        foreach (var set in result.Sets)
        {
            foreach (var value in set.Ordered)
            {
                if (s_hidden.Contains(value.Name) || value.Name.Equals("JOB_STATUS", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var display = value.Kind == ResultKind.Integer && value.Name.EndsWith("_ADR", StringComparison.OrdinalIgnoreCase)
                    ? $"0x{value.AsLong:X2}"
                    : value.Display;
                items.Add(new IdentItem(value.Name, IdentLabels.For(value.Name), display));
            }
        }

        return items;
    }

    /// <summary>
    /// Extracts live values from a STATUS_* job. STAT_X_WERT is paired with STAT_X_EINH; other STAT_ values
    /// are shown as-is. Falls back to every visible result if the SGBD does not use the STAT_ convention.
    /// </summary>
    public static IReadOnlyList<LiveValue> ParseLive(JobResult result)
    {
        var values = new List<LiveValue>();
        foreach (var set in result.Sets)
        {
            var keys = set.Keys.ToList();
            var statKeys = keys.Where(k => k.StartsWith("STAT_", StringComparison.OrdinalIgnoreCase)).ToList();
            var source = statKeys.Count > 0 ? statKeys : keys.Where(k => !s_hidden.Contains(k) && !k.Equals("JOB_STATUS", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var key in source)
            {
                if (key.EndsWith("_EINH", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("STAT_ID_SG_ADR", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = set[key];
                var stem = key.EndsWith("_WERT", StringComparison.OrdinalIgnoreCase) ? key[..^5] : key;
                var unit = Clean(set.Text(stem + "_EINH"));
                values.Add(new LiveValue(
                    result.Job,
                    key,
                    Translation.Translator.Current.Enabled
                        ? Translation.JobNames.Result(result.Variant, result.Job, key)
                        : Humanize(stem),
                    value.Display,
                    unit is "-" ? null : unit,
                    value.Kind is ResultKind.Integer or ResultKind.Real ? value.AsDouble : null));
            }
        }

        return values;
    }

    /// <summary>Fallback when F_VORHANDEN_NR is missing: interprets the German/English SGBD text.</summary>
    internal static bool? InterpretPresent(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.ToLowerInvariant();
        if (t.Contains("momentan nicht vorhanden") || t.Contains("bisher nicht aufgetreten") ||
            t.Contains("currently not present") || t.Contains("not present") || t.Contains("not occurred"))
        {
            return false;
        }

        if (t.Contains("momentan vorhanden") || t.Contains("currently present") || t.Contains("present"))
        {
            return true;
        }

        return null;
    }

    /// <summary>Fallback when F_WARNUNG_NR is missing: interprets the warning lamp text.</summary>
    internal static bool? InterpretWarning(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.ToLowerInvariant();
        if (t.Contains("kein aufleuchten") || t.Contains("keine warnlampe") || t.Contains("would not") || t.Contains("no warning"))
        {
            return false;
        }

        if (t.Contains("aufleuchten") || t.Contains("warnlampe") || t.Contains("warning lamp") || t.Contains("would cause"))
        {
            return true;
        }

        return null;
    }

    /// <summary>STAT_MOTORTEMPERATUR -> "Motortemperatur".</summary>
    internal static string Humanize(string key)
    {
        var s = key.StartsWith("STAT_", StringComparison.OrdinalIgnoreCase) ? key[5..]
            : key.StartsWith("ID_", StringComparison.OrdinalIgnoreCase) ? key[3..]
            : key;
        s = s.Replace('_', ' ').Trim();
        if (s.Length == 0)
        {
            return key;
        }

        return char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        return t is "--" or "?" ? null : t;
    }

    private static List<KeyValuePair<string, string>> Visible(ResultSet set) =>
        set.Ordered
            .Where(v => !s_hidden.Contains(v.Name))
            .Select(v => new KeyValuePair<string, string>(v.Name, v.Display))
            .ToList();

    [GeneratedRegex(@"^F_UW(\d+)_(TEXT|WERT|EINH|NR)$", RegexOptions.IgnoreCase)]
    private static partial Regex UwRegex();

    [GeneratedRegex(@"^F_ART(\d+)_TEXT$", RegexOptions.IgnoreCase)]
    private static partial Regex ArtRegex();
}

/// <summary>Human labels for common BMW identification result names.</summary>
public static class IdentLabels
{
    private static readonly Dictionary<string, string> s_labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ID_BMW_NR"] = "BMW part number",
        ["ID_HW_NR"] = "Hardware number",
        ["ID_COD_INDEX"] = "Coding index",
        ["ID_DIAG_INDEX"] = "Diagnosis index",
        ["ID_BUS_INDEX"] = "Bus index",
        ["ID_VAR_INDEX"] = "Variant index",
        ["ID_DATUM"] = "Manufacturing date",
        ["ID_DATUM_JAHR"] = "Manufacturing year",
        ["ID_DATUM_MONAT"] = "Manufacturing month",
        ["ID_DATUM_TAG"] = "Manufacturing day",
        ["ID_DATUM_KW"] = "Manufacturing week",
        ["ID_LIEF_NR"] = "Supplier number",
        ["ID_LIEF_TEXT"] = "Supplier",
        ["ID_SW_NR"] = "Software number",
        ["ID_SW_NR_MCV"] = "Software (message catalogue)",
        ["ID_SW_NR_FSV"] = "Software (functional)",
        ["ID_SW_NR_OSV"] = "Software (operating system)",
        ["ID_SW_NR_RES"] = "Software (reserved)",
        ["ID_SG_ADR"] = "ECU address",
        ["ID_LIN_SLAVE_ADR"] = "LIN slave address",
        ["ID_EWS_SS"] = "EWS interface",
        ["ID_PROD_NR"] = "Production number",
        ["ID_SERIEN_NR"] = "Serial number",
        ["VARIANTE"] = "Variant",
    };

    public static string For(string key) =>
        s_labels.TryGetValue(key, out var label) ? label : Translation.Translator.Current.Translate(FaultParser.Humanize(key));
}
