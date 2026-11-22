using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace BmwDecode.Core.Data;

/// <summary>Writes an <see cref="SgbdPack"/>. Used by tools/BmwDecode.DataBuilder and by tests.</summary>
public sealed class SgbdPackWriter : IDisposable
{
    private readonly ZipArchive _zip;
    private readonly List<PackIndexEntry> _index = [];
    private readonly HashSet<string> _texts = new(StringComparer.Ordinal);
    private readonly PackManifest _manifest;

    public SgbdPackWriter(Stream output, string source)
    {
        _zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false);
        _manifest = new PackManifest { Format = SgbdPack.FormatVersion, Built = DateTimeOffset.UtcNow, Source = source };
    }

    public PackManifest Manifest => _manifest;

    public void Add(PackedSgbd sgbd)
    {
        var entry = _zip.CreateEntry(SgbdPack.EntryName(sgbd.Name), CompressionLevel.SmallestSize);
        using (var s = entry.Open())
        {
            JsonSerializer.Serialize(s, sgbd, SgbdPack.Json);
        }

        _index.Add(new PackIndexEntry
        {
            Name = sgbd.Name.ToUpperInvariant(),
            IsGroup = sgbd.IsGroup,
            JobCount = sgbd.Jobs.Count,
            Origin = sgbd.Version.GetValueOrDefault("AUTHOR") is { Length: > 0 } a ? a : null,
        });
        _manifest.SgbdCount++;
        _manifest.JobCount += sgbd.Jobs.Count;
        _manifest.TableCount += sgbd.Tables.Count;

        foreach (var job in sgbd.Jobs)
        {
            Collect(job.Comments);
            foreach (var p in job.Arguments.Concat(job.Results))
            {
                Collect(p.Comments);
            }
        }

        foreach (var rows in sgbd.Tables.Values)
        {
            foreach (var row in rows.Skip(1))
            {
                Collect(row);
            }
        }
    }

    public void Failed(string sgbd, string reason) => _manifest.Failed.Add($"{sgbd}: {reason}");

    /// <summary>Adds a raw entry (used for the ista/ section).</summary>
    public void AddEntry(string name, Action<Stream> write, CompressionLevel level = CompressionLevel.SmallestSize)
    {
        using var s = _zip.CreateEntry(name, level).Open();
        write(s);
    }

    public void AddJson<T>(string name, T value) => AddEntry(name, s => JsonSerializer.Serialize(s, value, SgbdPack.Json));

    /// <summary>Every distinct text that might be German (has lowercase letters), for translation.</summary>
    public IReadOnlyCollection<string> Texts => _texts;

    /// <summary>Writes translations, index and manifest, then closes the archive.</summary>
    public void Complete(Func<string, string> translate)
    {
        var tsv = _zip.CreateEntry("translations.tsv", CompressionLevel.SmallestSize);
        using (var w = new StreamWriter(tsv.Open(), new UTF8Encoding(false)))
        {
            foreach (var text in _texts.OrderBy(t => t, StringComparer.Ordinal))
            {
                var en = translate(text);
                if (!string.IsNullOrEmpty(en) && !en.Equals(text, StringComparison.Ordinal))
                {
                    w.Write(SgbdPack.Escape(text));
                    w.Write('\t');
                    w.Write(SgbdPack.Escape(en));
                    w.Write('\n');
                    _manifest.TranslationCount++;
                }
            }
        }

        Write("index.json", _index.OrderBy(e => e.Name, StringComparer.Ordinal).ToList());
        Write("manifest.json", _manifest);
        _zip.Dispose();
    }

    public void Dispose() => _zip.Dispose();

    private void Write<T>(string name, T value)
    {
        using var s = _zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
        JsonSerializer.Serialize(s, value, SgbdPack.Json);
    }

    private void Collect(IEnumerable<string> texts)
    {
        foreach (var t in texts)
        {
            if (IsText(t))
            {
                _texts.Add(t.Trim());
            }
        }
    }

    /// <summary>Worth translating: contains a lowercase letter (identifiers and codes are all caps / digits).</summary>
    internal static bool IsText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length < 2)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (char.IsLower(c))
            {
                return true;
            }
        }

        return false;
    }
}
