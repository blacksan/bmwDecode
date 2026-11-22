using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace BmwDecode.Core.Translation;

/// <summary>
/// Offline German → English translation of SGBD texts (fault locations, fault types, environment conditions, job and
/// result descriptions). Order: user overrides → exact phrase table → word-by-word glossary with compound splitting.
/// Codes, abbreviations, numbers and English text pass through unchanged.
/// </summary>
public sealed partial class Translator
{
    private readonly Dictionary<string, string> _phrases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _words = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _overrides = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<string, string>? _exact;
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private int _maxWordLength;

    // Declared before Current: static initialisers run in textual order.
    public static Translator Passthrough { get; } = new() { Enabled = false };

    /// <summary>Translator used by the parsers. Passthrough until the app (or a test) installs one.</summary>
    public static Translator Current { get; set; } = Passthrough;

    public bool Enabled { get; set; } = true;

    public int PhraseCount => _phrases.Count;

    /// <summary>Precomputed translations of the SGBD texts from the data pack (exact text match).</summary>
    public int ExactCount => _exact?.Count ?? 0;

    /// <summary>
    /// Uses the data pack's precomputed SGBD translations, checked before the phrase table and word glossary.
    /// The user's override file still wins.
    /// </summary>
    public void UseExact(IReadOnlyDictionary<string, string>? exact)
    {
        lock (_lock)
        {
            _exact = exact;
            _cache.Clear();
        }
    }
    public int WordCount => _words.Count;

    /// <summary>Translator with the built-in phrase table and glossary, plus an optional user override file.</summary>
    public static Translator CreateDefault(string? overridePath = null)
    {
        var t = new Translator();
        var asm = typeof(Translator).Assembly;
        t.LoadTsv(Open(asm, "BmwDecode.Core.Translation.glossary.tsv"), t._words, key: NormalizeWord);
        t.LoadTsv(Open(asm, "BmwDecode.Core.Translation.phrases.tsv"), t._phrases, key: NormalizePhrase);
        if (overridePath is not null && File.Exists(overridePath))
        {
            using var reader = new StreamReader(overridePath);
            t.LoadTsv(reader, t._overrides, key: NormalizePhrase);
        }

        t._maxWordLength = t._words.Keys.DefaultIfEmpty(string.Empty).Max(k => k.Length);
        return t;
    }

    /// <summary>For tests and tools: build from explicit tables.</summary>
    public static Translator FromTables(IEnumerable<KeyValuePair<string, string>> words, IEnumerable<KeyValuePair<string, string>> phrases)
    {
        var t = new Translator();
        foreach (var w in words)
        {
            t._words[NormalizeWord(w.Key)] = w.Value;
        }

        foreach (var p in phrases)
        {
            t._phrases[NormalizePhrase(p.Key)] = p.Value;
        }

        t._maxWordLength = t._words.Keys.DefaultIfEmpty(string.Empty).Max(k => k.Length);
        return t;
    }

    /// <summary>Translates one text. Returns the input unchanged when disabled or when nothing German is found.</summary>
    public string Translate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Enabled)
        {
            return text ?? string.Empty;
        }

        lock (_lock)
        {
            if (_cache.TryGetValue(text, out var cached))
            {
                return cached;
            }

            var result = TranslateCore(text.Trim());
            if (_cache.Count < 200_000)
            {
                _cache[text] = result;
            }

            return result;
        }
    }

    /// <summary>Translates when needed and returns null for null input (convenient for optional fields).</summary>
    public string? TranslateOrNull(string? text) => text is null ? null : Translate(text);

    /// <summary>EDIABAS naming conventions that are not ordinary German words.</summary>
    private static readonly Dictionary<string, string> s_jobTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["status"] = "status", ["stat"] = "status", ["steuern"] = "control", ["ansteuern"] = "activate",
        ["lesen"] = "read", ["schreiben"] = "write", ["loeschen"] = "clear", ["löschen"] = "clear",
        ["ist"] = "actual", ["soll"] = "target", ["wert"] = "value", ["werte"] = "values", ["einh"] = "unit",
        ["fs"] = "fault memory", ["is"] = "info memory", ["hs"] = "history memory", ["c"] = "coding",
        ["sg"] = "ECU", ["nr"] = "no.", ["anz"] = "count", ["ende"] = "end", ["start"] = "start", ["stop"] = "stop",
        ["reset"] = "reset", ["ident"] = "identification", ["info"] = "info", ["text"] = "text", ["vorgabe"] = "preset",
        ["adaption"] = "adaptation", ["adaptionen"] = "adaptations", ["abgleich"] = "calibration", ["zustand"] = "state",
        ["fa"] = "vehicle order", ["zcs"] = "ZCS", ["fg"] = "VIN", ["aif"] = "programming record", ["prog"] = "programming",
        ["ein"] = "on", ["aus"] = "off", ["an"] = "on", ["akt"] = "current", ["min"] = "min", ["max"] = "max",
        ["e"] = "electric", ["v"] = "V", ["temp"] = "temperature", ["ubatt"] = "battery voltage", ["kl"] = "terminal",
        ["daten"] = "data", ["lang"] = "long", ["kurz"] = "short", ["detail"] = "details", ["funktional"] = "functional",
        ["programmieren"] = "program", ["auftrag"] = "job", ["ruecksetzen"] = "reset", ["lernwerte"] = "learned values",
        ["messemode"] = "showroom mode", ["pruefstempel"] = "test stamp", ["luftemperatur"] = "air temperature",
        ["setzen"] = "set", ["quittieren"] = "acknowledge", ["freigabe"] = "enable", ["sperren"] = "lock",
        ["entsperren"] = "unlock", ["anlernen"] = "teach in", ["kalibrieren"] = "calibrate", ["pruefen"] = "check",
        ["test"] = "test", ["selbsttest"] = "self-test", ["energiesparmode"] = "energy-saving mode", ["fertigungsmode"] = "production mode",
        ["transportmode"] = "transport mode", ["flashmode"] = "flash mode", ["diagnosemode"] = "diagnostic mode",
    };

    /// <summary>EDIABAS names spell umlauts as ae / oe / ue; the glossary uses the real letters.</summary>
    private string? TranslateIdentifierWord(string lower)
    {
        var word = TranslateWord(lower);
        if (!string.IsNullOrEmpty(word) || !(lower.Contains("ae") || lower.Contains("oe") || lower.Contains("ue")))
        {
            return word;
        }

        var unfolded = lower.Replace("ae", "ä", StringComparison.Ordinal).Replace("oe", "ö", StringComparison.Ordinal)
            .Replace("ue", "ü", StringComparison.Ordinal);
        return TranslateWord(unfolded);
    }

    /// <summary>
    /// Readable English for an EDIABAS job or result name: STATUS_RAILDRUCK_IST → "Status rail pressure actual",
    /// STEUERN_E_LUEFTER → "Control electric fan". Abbreviations it does not know stay as they are.
    /// </summary>
    public string TranslateIdentifier(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !Enabled)
        {
            return name ?? string.Empty;
        }

        var words = new List<string>();
        foreach (var raw in name.Trim('_').Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            var lower = raw.ToLowerInvariant();
            if (s_jobTerms.TryGetValue(lower, out var term))
            {
                words.Add(term);
                continue;
            }

            if (raw.All(char.IsDigit) || (raw.Length <= 3 && raw.Any(char.IsLetter)))
            {
                words.Add(raw); // numbers and short codes (DME, CAN, AGR, 0x12)
                continue;
            }

            var word = TranslateIdentifierWord(lower);
            words.Add(string.IsNullOrEmpty(word) ? raw : word);
        }

        // C_C_LESEN would read "coding coding read": drop a word that repeats the one before it.
        var text = string.Join(' ', words.Where((w, i) => w.Length > 0 && (i == 0 || !w.Equals(words[i - 1], StringComparison.OrdinalIgnoreCase))));
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private string TranslateCore(string text)
    {
        var key = NormalizePhrase(text);
        if (_overrides.TryGetValue(key, out var o))
        {
            return o;
        }

        if (_exact is not null && _exact.TryGetValue(text, out var exact))
        {
            return exact;
        }

        if (_phrases.TryGetValue(key, out var p))
        {
            return MatchCase(text, p);
        }

        // Collocations the word-by-word pass would get wrong: "zu hoch" = too high (not "to high").
        var prepared = TooRegex().Replace(text, "\u0001");

        var sb = new StringBuilder(text.Length + 16);
        var translatedAny = false;
        char previous = ' ';
        foreach (Match m in TokenRegex().Matches(prepared))
        {
            var token = m.Value;
            if (!char.IsLetter(token[0]))
            {
                sb.Append(token.Replace("\u0001", "too ", StringComparison.Ordinal));
                translatedAny |= token.Contains('\u0001');
                previous = token[^1];
                continue;
            }

            // Letters glued to digits are part of a code (0x2FC, 272.4.8, 46CB): leave them alone.
            if (char.IsDigit(previous) || (m.Index + token.Length < prepared.Length && char.IsDigit(prepared[m.Index + token.Length]) && token.Length <= 3))
            {
                sb.Append(token);
                previous = token[^1];
                continue;
            }

            var before = m.Index > 0 ? prepared[m.Index - 1] : ' ';
            var after = m.Index + token.Length < prepared.Length ? prepared[m.Index + token.Length] : ' ';
            previous = token[^1];

            // Identifiers (STATUS_MOTORDREHZAHL, KLEMME_15) and all-caps codes (DSCIII) are never translated.
            if (before == '_' || after == '_' || (token.Length > 1 && token.All(char.IsUpper)))
            {
                sb.Append(token);
                continue;
            }

            // BMW uses capitalised Ist/Soll for actual/target values (Ist-Gang, Soll Drehzahl).
            if (token is "Ist" && (m.Index > 0 || after == '-'))
            {
                sb.Append("actual");
                translatedAny = true;
                continue;
            }

            if (token is "Soll")
            {
                sb.Append("target");
                translatedAny = true;
                continue;
            }

            var english = TranslateWord(token);
            if (english is not null && english.Equals(token, StringComparison.OrdinalIgnoreCase) && token.Skip(1).Any(char.IsUpper))
            {
                english = token; // codes such as CAN, DSC, PWG: keep their spelling
            }

            if (english is null)
            {
                sb.Append(token);
                continue;
            }

            translatedAny |= !english.Equals(token, StringComparison.OrdinalIgnoreCase);
            if (english.Length == 0)
            {
                // Dropped article: avoid double spaces.
                if (sb.Length > 0 && sb[^1] == ' ')
                {
                    sb.Length--;
                }

                continue;
            }

            sb.Append(english);
        }

        if (!translatedAny)
        {
            return text;
        }

        var result = SpaceRegex().Replace(sb.ToString(), " ").Trim();
        result = SpaceBeforePunctuationRegex().Replace(result, "$1");
        return MatchCase(text, result);
    }

    private static readonly HashSet<string> s_articles = new(StringComparer.Ordinal)
    {
        // "ein" is deliberately missing: in SGBD texts it usually means "on" (Schalter ein).
        "der", "die", "das", "des", "dem", "den", "eine", "einer", "eines", "einem", "einen",
    };

    private string? TranslateWord(string token)
    {
        // Abbreviations and codes (DME, CAN, PT-CAN, F01) stay as they are.
        if (token.Length <= 5 && token.All(c => char.IsUpper(c) || char.IsDigit(c)))
        {
            return token;
        }

        var w = NormalizeWord(token);
        if (s_articles.Contains(w))
        {
            return string.Empty;
        }

        if (_words.TryGetValue(w, out var e))
        {
            return e;
        }

        var folded = FoldUmlauts(w);
        if (folded != w && _words.TryGetValue(folded, out e))
        {
            return e;
        }

        return SplitCompound(folded, 0);
    }

    /// <summary>Splits German compounds greedily: "kuehlmittelpumpe" → "kühlmittel" + "pumpe" → "coolant pump".</summary>
    private string? SplitCompound(string word, int depth)
    {
        if (depth > 4 || word.Length < 6)
        {
            return null;
        }

        for (var len = Math.Min(word.Length - 3, _maxWordLength); len >= 3; len--)
        {
            var head = word[..len];
            if (!_words.TryGetValue(head, out var headEn))
            {
                continue;
            }

            var rest = word[len..];

            // Linking elements between compound parts (Fugen-s / -n / -en / -es).
            foreach (var link in new[] { string.Empty, "s", "es", "n", "en" })
            {
                if (!rest.StartsWith(link, StringComparison.Ordinal))
                {
                    continue;
                }

                var tail = rest[link.Length..];
                if (tail.Length < 3)
                {
                    continue;
                }

                var tailEn = _words.TryGetValue(tail, out var t) ? t : SplitCompound(tail, depth + 1);
                if (tailEn is not null)
                {
                    return $"{headEn} {tailEn}";
                }
            }
        }

        return null;
    }

    private void LoadTsv(TextReader? reader, Dictionary<string, string> target, Func<string, string> key)
    {
        if (reader is null)
        {
            return;
        }

        using (reader)
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var tab = line.IndexOf('\t', StringComparison.Ordinal);
                if (tab <= 0 || tab == line.Length - 1)
                {
                    continue;
                }

                var german = line[..tab];
                var english = line[(tab + 1)..].Trim();
                if (english.Length > 0)
                {
                    target[key(german)] = english;
                }
            }
        }
    }

    private static TextReader? Open(Assembly asm, string name) =>
        asm.GetManifestResourceStream(name) is { } s ? new StreamReader(s, Encoding.UTF8) : null;

    internal static string NormalizeWord(string w) => FoldUmlauts(w.Trim().ToLowerInvariant());

    internal static string NormalizePhrase(string p) => FoldUmlauts(SpaceRegex().Replace(p.Trim(), " ").ToLowerInvariant());

    /// <summary>SGBDs spell umlauts both ways (erfüllt / erfuellt); fold both to the umlaut form for lookups.</summary>
    internal static string FoldUmlauts(string s) =>
        s.Replace("ae", "ä", StringComparison.Ordinal).Replace("oe", "ö", StringComparison.Ordinal).Replace("ue", "ü", StringComparison.Ordinal);

    private static string MatchCase(string original, string translated)
    {
        if (translated.Length == 0)
        {
            return translated;
        }

        return char.IsUpper(original[0]) && char.IsLower(translated[0])
            ? char.ToUpper(translated[0], CultureInfo.InvariantCulture) + translated[1..]
            : translated;
    }

    [GeneratedRegex(@"[\p{L}]+|[^\p{L}]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"\b[Zz]u\s+(?=(hoch|niedrig|gering|groß|gross|klein|lang|kurz|schnell|langsam|warm|kalt|heiß|heiss|früh|frueh|spät|spaet|stark|schwach|fett|mager|viel|wenig|weit|nah|oft|häufig|haeufig)\w*\b)")]
    private static partial Regex TooRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"\s+([,.;:)])")]
    private static partial Regex SpaceBeforePunctuationRegex();
}
