using System.Globalization;
using BmwDecode.Core.Diagnostics;

namespace BmwDecode.Core.Vehicle;

/// <summary>What a BMW type key (VIN positions 4–7) stands for.</summary>
public sealed record TypeKeyInfo(
    string TypeKey,
    string? Series,
    string? Model,
    string? Engine,
    string? Drive,
    string? Gearbox,
    string? Displacement,
    string? Body,
    string? Power,
    string? Steering,
    string? Doors,
    string? Market,
    string? Production,
    string Source);

/// <summary>
/// Type key lookup assembled at runtime from data the user already has: the FZGIDENT.PRG tables of their EDIABAS
/// install (strong for E36/E38) and an optional typekeys.csv they maintain. No BMW data ships with the app.
/// </summary>
public sealed class TypeKeyDatabase
{
    public const string CsvHeader = "type_key,series,model,engine,drive,gearbox,displacement,body,power,steering,doors,market,production";

    private readonly Dictionary<string, TypeKeyInfo> _byKey;

    private TypeKeyDatabase(IEnumerable<TypeKeyInfo> entries, IReadOnlyList<string> sources)
    {
        _byKey = new Dictionary<string, TypeKeyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            _byKey[e.TypeKey] = e; // later sources (the user's CSV) override earlier ones
        }

        Sources = sources;
    }

    public static TypeKeyDatabase Empty { get; } = new([], []);

    public int Count => _byKey.Count;

    public IReadOnlyList<string> Sources { get; }

    public TypeKeyInfo? Find(string? typeKey) =>
        typeKey is { Length: 4 } && _byKey.TryGetValue(typeKey, out var info) ? info : null;

    public static async Task<TypeKeyDatabase> LoadAsync(ISgbdInspector? inspector, string? csvPath, CancellationToken cancellationToken = default)
    {
        var entries = new List<TypeKeyInfo>();
        var sources = new List<string>();

        if (inspector is { IsAvailable: true })
        {
            var gm = FromFzgIdent(await inspector.ReadTableAsync("FZGIDENT", "GM_TEXTE", cancellationToken).ConfigureAwait(false)).ToList();
            var typ = FromTypTexte(await inspector.ReadTableAsync("FZGIDENT", "TYP_TEXTE", cancellationToken).ConfigureAwait(false));
            var known = gm.Select(g => g.TypeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            entries.AddRange(gm);
            entries.AddRange(typ.Where(t => !known.Contains(t.TypeKey)));
            if (entries.Count > 0)
            {
                sources.Add($"FZGIDENT.PRG ({entries.Count} type keys)");
            }
        }

        if (!string.IsNullOrWhiteSpace(csvPath) && File.Exists(csvPath))
        {
            using var reader = new StreamReader(csvPath);
            var csv = FromCsv(reader, Path.GetFileName(csvPath)).ToList();
            entries.AddRange(csv);
            sources.Add($"{Path.GetFileName(csvPath)} ({csv.Count} type keys)");
        }

        return new TypeKeyDatabase(entries, sources);
    }

    /// <summary>FZGIDENT GM_TEXTE: GMTYP, TYP, BR, VERK, MOTOR, ANTR, GETR, HUBR, KAROS, LEIST, LENK, TUER, LAND, REIHE.</summary>
    internal static IEnumerable<TypeKeyInfo> FromFzgIdent(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count < 2)
        {
            yield break;
        }

        var c = Columns(rows[0]);
        foreach (var r in rows.Skip(1))
        {
            var key = Get(r, c, "TYP");
            if (key is not { Length: 4 })
            {
                continue;
            }

            var hubr = Get(r, c, "HUBR");
            if (hubr is not null && double.TryParse(hubr, NumberStyles.Float, CultureInfo.InvariantCulture, out var dl) && dl > 0)
            {
                hubr = $"{dl / 10.0:0.0} L"; // FZGIDENT stores decilitres: 25.00 = 2.5 L
            }

            var kw = Get(r, c, "LEIST") is { } p && p != "0" ? p : null;
            yield return new TypeKeyInfo(key.ToUpperInvariant(), Get(r, c, "BR"), Get(r, c, "VERK"), Get(r, c, "MOTOR"),
                Get(r, c, "ANTR"), Get(r, c, "GETR"), hubr, Get(r, c, "KAROS"), kw is null ? null : kw + " kW",
                Get(r, c, "LENK"), Get(r, c, "TUER"), Get(r, c, "LAND"), null, "FZGIDENT.PRG");
        }
    }

    /// <summary>FZGIDENT TYP_TEXTE: GMTYP, TYP, BAUREIHE, MODELL, LA, ...</summary>
    internal static IEnumerable<TypeKeyInfo> FromTypTexte(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count < 2)
        {
            yield break;
        }

        var c = Columns(rows[0]);
        foreach (var r in rows.Skip(1))
        {
            if (Get(r, c, "TYP") is { Length: 4 } key)
            {
                yield return new TypeKeyInfo(key.ToUpperInvariant(), Get(r, c, "BAUREIHE"), Get(r, c, "MODELL")?.Replace('_', ' '),
                    null, null, null, null, null, null, null, null, Get(r, c, "LA"), null, "FZGIDENT.PRG");
            }
        }
    }

    /// <summary>User CSV in the <see cref="CsvHeader"/> layout. Lines starting with # are comments.</summary>
    internal static IEnumerable<TypeKeyInfo> FromCsv(TextReader reader, string source)
    {
        string? line;
        Dictionary<string, int>? c = null;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var cells = SplitCsv(line);
            if (c is null)
            {
                c = Columns(cells.Select(h => h.Trim().ToUpperInvariant()).ToList());
                if (!c.ContainsKey("TYPE_KEY"))
                {
                    c = Columns(CsvHeader.ToUpperInvariant().Split(','));
                }
                else
                {
                    continue;
                }
            }

            if (Get(cells, c, "TYPE_KEY") is { Length: 4 } key)
            {
                yield return new TypeKeyInfo(key.ToUpperInvariant(), Get(cells, c, "SERIES"), Get(cells, c, "MODEL"), Get(cells, c, "ENGINE"),
                    Get(cells, c, "DRIVE"), Get(cells, c, "GEARBOX"), Get(cells, c, "DISPLACEMENT"), Get(cells, c, "BODY"),
                    Get(cells, c, "POWER"), Get(cells, c, "STEERING"), Get(cells, c, "DOORS"), Get(cells, c, "MARKET"),
                    Get(cells, c, "PRODUCTION"), source);
            }
        }
    }

    private static Dictionary<string, int> Columns(IReadOnlyList<string> header) =>
        header.Select((h, i) => (h.Trim().ToUpperInvariant(), i)).GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.First().i);

    private static string? Get(IReadOnlyList<string> row, Dictionary<string, int> columns, string name) =>
        columns.TryGetValue(name, out var i) && i < row.Count && row[i].Trim() is { Length: > 0 } v && v is not ("-" or "--" or "X") ? v : null;

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else if (ch == '"')
                {
                    quoted = false;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '"')
            {
                quoted = true;
            }
            else if (ch is ',' or ';')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }
}
