using System.Collections.Concurrent;
using BmwDecode.Core.Data;

namespace BmwDecode.Core.Translation;

/// <summary>
/// English names for an ECU's jobs and results: BMW's own titles from the data pack (ISTA's function list for that ECU
/// variant) where they exist, otherwise a word-by-word translation of the EDIABAS name.
/// </summary>
public static class JobNames
{
    private static readonly ConcurrentDictionary<string, Titles> s_byVariant = new(StringComparer.OrdinalIgnoreCase);

    public static string Job(string? variant, string job)
    {
        var titles = For(variant);
        return titles?.Jobs.TryGetValue(job, out var t) == true ? t : Translator.Current.TranslateIdentifier(job);
    }

    public static string Result(string? variant, string job, string result)
    {
        var titles = For(variant);
        if (titles?.Results.TryGetValue((job, result), out var t) == true || titles?.ResultsAnyJob.TryGetValue(result, out t) == true)
        {
            return t!;
        }

        // STAT_RAILDRUCK_IST_WERT -> "Rail pressure actual": the STAT_ prefix and _WERT suffix carry no meaning.
        var stem = result;
        if (stem.StartsWith("STAT_", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[5..];
        }

        if (stem.EndsWith("_WERT", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^5];
        }

        return Translator.Current.TranslateIdentifier(stem.Length > 0 ? stem : result);
    }

    /// <summary>Drop cached titles (after a new data pack was installed).</summary>
    public static void Reset() => s_byVariant.Clear();

    private static Titles? For(string? variant)
    {
        if (string.IsNullOrWhiteSpace(variant) || !Translator.Current.Enabled || ReferenceData.Ista is not { } ista)
        {
            return null;
        }

        return s_byVariant.GetOrAdd(variant, v =>
        {
            var titles = new Titles();
            try
            {
                foreach (var fn in ista.Ecu(v)?.Functions ?? [])
                {
                    foreach (var job in fn.Jobs)
                    {
                        // One title per job: prefer the function that calls it without arguments.
                        if (fn.Title.Length > 0 && (!titles.Jobs.ContainsKey(job.Name) || job.Arguments.Length == 0))
                        {
                            titles.Jobs[job.Name] = fn.Title;
                        }

                        foreach (var r in job.Results.Where(r => r.Title.Length > 0))
                        {
                            titles.Results.TryAdd((job.Name, r.Name), r.Title);
                            titles.ResultsAnyJob.TryAdd(r.Name, r.Title);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException or System.Text.Json.JsonException)
            {
                // Without the pack's titles the translated names are used.
            }

            return titles;
        });
    }

    private sealed class Titles
    {
        public Dictionary<string, string> Jobs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(string Job, string Result), string> Results { get; } = new(TupleComparer.Instance);
        public Dictionary<string, string> ResultsAnyJob { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class TupleComparer : IEqualityComparer<(string Job, string Result)>
    {
        public static readonly TupleComparer Instance = new();

        public bool Equals((string Job, string Result) x, (string Job, string Result) y) =>
            string.Equals(x.Job, y.Job, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Result, y.Result, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Job, string Result) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Job), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Result));
    }
}
