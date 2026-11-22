using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BmwDecode.Core.Data;

/// <summary>
/// Offline copy of everything bmwDecode needs from the SGBD files: every job with its comments, arguments and
/// results, every table (fault texts, fault types, freeze-frame definitions, lookup tables), version info, and a
/// precomputed German to English translation of every text. Built once by tools/BmwDecode.DataBuilder from the
/// Ecu folder and shipped with the private installer, so descriptions and English texts never depend on
/// INPA/ISTA or on re-reading the .prg files.
///
/// Layout (zip): manifest.json, index.json, sgbd/&lt;NAME&gt;.json per SGBD, translations.tsv (de TAB en).
/// The pack is derived from BMW's SGBD files: keep it with the private build, never in the public repo.
/// </summary>
public sealed class SgbdPack : IDisposable
{
    public const string FileName = "sgbd.pack";
    public const int FormatVersion = 1;

    internal static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ZipArchive _zip;
    private readonly object _zipLock = new();
    private readonly ConcurrentDictionary<string, PackedSgbd?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackIndexEntry> _index;
    private Dictionary<string, string>? _translations;

    private SgbdPack(ZipArchive zip, string path)
    {
        _zip = zip;
        Path = path;
        Manifest = Read<PackManifest>("manifest.json") ?? new PackManifest();
        _index = (Read<List<PackIndexEntry>>("index.json") ?? [])
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public string Path { get; }

    public PackManifest Manifest { get; }

    public IReadOnlyCollection<PackIndexEntry> Index => _index.Values;

    public int Count => _index.Count;

    public static SgbdPack Open(string path)
    {
        var stream = File.OpenRead(path);
        try
        {
            return new SgbdPack(new ZipArchive(stream, ZipArchiveMode.Read), path);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens the first pack found in the usual install locations, or null.</summary>
    public static SgbdPack? TryOpenDefault(params string?[] extraFolders)
    {
        foreach (var folder in DefaultFolders(extraFolders))
        {
            var p = System.IO.Path.Combine(folder, FileName);
            if (!File.Exists(p))
            {
                continue;
            }

            try
            {
                return Open(p);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
            {
                // Corrupt or locked pack: try the next location.
            }
        }

        return null;
    }

    public static IEnumerable<string> DefaultFolders(params string?[] extraFolders)
    {
        foreach (var f in extraFolders)
        {
            if (!string.IsNullOrWhiteSpace(f))
            {
                yield return f;
            }
        }

        yield return System.IO.Path.Combine(AppContext.BaseDirectory, "Data");
        yield return AppContext.BaseDirectory;
        yield return System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "bmwDecode", "Data");
    }

    /// <summary>ISTA-derived texts, live values, type keys and VIN ranges; null when the pack was built without them.</summary>
    public IstaData? Ista => _ista ??= LoadIsta();

    private IstaData? _ista;

    private IstaData? LoadIsta()
    {
        var manifest = Read<IstaManifest>("ista/manifest.json");
        return manifest is null ? null : new IstaData(OpenCopy, manifest);
    }

    /// <summary>A private copy of one entry: zip streams share the file handle and are not thread-safe.</summary>
    private Stream? OpenCopy(string entry)
    {
        lock (_zipLock)
        {
            var e = _zip.GetEntry(entry);
            if (e is null)
            {
                return null;
            }

            var ms = new MemoryStream(e.Length > 0 && e.Length < int.MaxValue ? (int)e.Length : 0);
            using (var s = e.Open())
            {
                s.CopyTo(ms);
            }

            ms.Position = 0;
            return ms;
        }
    }

    public bool Contains(string sgbd) => _index.ContainsKey(sgbd);

    public PackedSgbd? Get(string sgbd) =>
        _cache.GetOrAdd(sgbd, name => _index.TryGetValue(name, out var e) ? Read<PackedSgbd>(EntryName(e.Name)) : null);

    /// <summary>German to English for every SGBD text whose translation differs from the original.</summary>
    public IReadOnlyDictionary<string, string> Translations => _translations ??= LoadTranslations();

    /// <summary>English text of an SGBD string, or the original when the pack has no translation for it.</summary>
    public string English(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return Translations.TryGetValue(text.Trim(), out var en) ? en : text;
    }

    public void Dispose() => _zip.Dispose();

    internal static string EntryName(string sgbd) => "sgbd/" + sgbd.ToUpperInvariant() + ".json";

    private T? Read<T>(string entry)
    {
        lock (_zipLock)
        {
            var e = _zip.GetEntry(entry);
            if (e is null)
            {
                return default;
            }

            using var s = e.Open();
            return JsonSerializer.Deserialize<T>(s, Json);
        }
    }

    private Dictionary<string, string> LoadTranslations()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        lock (_zipLock)
        {
            var e = _zip.GetEntry("translations.tsv");
            if (e is null)
            {
                return map;
            }

            using var reader = new StreamReader(e.Open(), Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                var tab = line.IndexOf('\t');
                if (tab > 0)
                {
                    map[Unescape(line[..tab])] = Unescape(line[(tab + 1)..]);
                }
            }
        }

        return map;
    }

    public static string Escape(string s) =>
        s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal)
         .Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    public static string Unescape(string s)
    {
        if (!s.Contains('\\'))
        {
            return s;
        }

        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                sb.Append(s[i] switch { 't' => '\t', 'r' => '\r', 'n' => '\n', _ => s[i] });
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }
}

public sealed class PackManifest
{
    [JsonPropertyName("format")] public int Format { get; set; }
    [JsonPropertyName("built")] public DateTimeOffset Built { get; set; }
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("sgbds")] public int SgbdCount { get; set; }
    [JsonPropertyName("jobs")] public int JobCount { get; set; }
    [JsonPropertyName("tables")] public int TableCount { get; set; }
    [JsonPropertyName("translations")] public int TranslationCount { get; set; }
    [JsonPropertyName("failed")] public List<string> Failed { get; set; } = [];
}

public sealed class PackIndexEntry
{
    [JsonPropertyName("n")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("g")] public bool IsGroup { get; set; }
    [JsonPropertyName("j")] public int JobCount { get; set; }
    [JsonPropertyName("o")] public string? Origin { get; set; }
}

public sealed class PackedSgbd
{
    [JsonPropertyName("n")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("g")] public bool IsGroup { get; set; }
    /// <summary>_VERSIONINFO results (BIP version, revision, author, ...).</summary>
    [JsonPropertyName("v")] public Dictionary<string, string> Version { get; set; } = [];
    [JsonPropertyName("j")] public List<PackedJob> Jobs { get; set; } = [];
    /// <summary>Table name to rows; the first row holds the column names.</summary>
    [JsonPropertyName("t")] public Dictionary<string, List<string[]>> Tables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public PackedJob? Job(string name) => Jobs.FirstOrDefault(j => j.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Table by name, ignoring case (JSON deserialisation does not keep the dictionary's comparer).</summary>
    public IReadOnlyList<string[]> Table(string name) =>
        Tables.TryGetValue(name, out var t) ? t
        : Tables.FirstOrDefault(kv => kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value ?? [];
}

public sealed class PackedJob
{
    [JsonPropertyName("n")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("c")] public List<string> Comments { get; set; } = [];
    [JsonPropertyName("a")] public List<PackedParameter> Arguments { get; set; } = [];
    [JsonPropertyName("r")] public List<PackedParameter> Results { get; set; } = [];
}

public sealed class PackedParameter
{
    [JsonPropertyName("n")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("t")] public string Type { get; set; } = string.Empty;
    [JsonPropertyName("c")] public List<string> Comments { get; set; } = [];
}
