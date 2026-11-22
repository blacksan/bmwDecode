using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Serialization;

namespace BmwDecode.Core.Data;

/// <summary>
/// The ISTA-derived part of the data pack, imported at build time from an ECU functions export
/// (typekeyinfo.txt, vinranges_*.txt, faultdata_en.xml, &lt;ecu&gt;.xml):
/// official English fault, fault-mode and freeze-frame texts per ECU variant, ISTA's labelled live values and
/// actuator tests, every type key, and VIN serial ranges for VIN-only identification.
///
/// Pack entries: ista/manifest.json, ista/labels.tsv, ista/ecu/&lt;VARIANT&gt;.json, ista/typekeys.tsv,
/// ista/vin/&lt;band&gt;.bin.
/// </summary>
public sealed class IstaData
{
    private readonly Func<string, Stream?> _open;
    private readonly ConcurrentDictionary<string, IstaEcu?> _ecus = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, IstaLabel>? _labels;
    private Dictionary<string, IReadOnlyDictionary<string, string>>? _typeKeys;
    private readonly object _lock = new();

    internal IstaData(Func<string, Stream?> open, IstaManifest manifest)
    {
        _open = open;
        Manifest = manifest;
    }

    public IstaManifest Manifest { get; }

    /// <summary>ISTA's data for one ECU variant (the same name as its SGBD, e.g. D60M57A0), or null.</summary>
    public IstaEcu? Ecu(string variant) =>
        _ecus.GetOrAdd(variant, v =>
        {
            using var s = _open("ista/ecu/" + v.ToUpperInvariant() + ".json");
            return s is null ? null : System.Text.Json.JsonSerializer.Deserialize<IstaEcu>(s, SgbdPack.Json);
        });

    public IstaLabel? Label(string? id) =>
        id is not null && Labels.TryGetValue(id, out var l) ? l : null;

    /// <summary>English fault text for a fault code (F_ORT_NR) of an ECU variant, or null.</summary>
    public string? FaultText(string variant, long code)
    {
        var ecu = Ecu(variant);
        var fault = ecu?.FaultByCode(code);
        return Label(fault?.Label)?.Title is { Length: > 0 } t ? t : null;
    }

    /// <summary>Type key (VIN positions 4–7 / FA type key) to ISTA's vehicle attributes, column names in English.</summary>
    public IReadOnlyDictionary<string, string>? TypeKey(string typeKey)
    {
        var map = TypeKeys;
        return map.TryGetValue(typeKey.Trim().ToUpperInvariant(), out var v) ? v : null;
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TypeKeys
    {
        get
        {
            lock (_lock)
            {
                return _typeKeys ??= LoadTypeKeys();
            }
        }
    }

    /// <summary>Finds the production range for a VIN (17 characters, or the 7-character short VIN).</summary>
    public VinRange? FindVin(string vin)
    {
        vin = vin.Trim().ToUpperInvariant();
        string vin7;
        string? vinType = null;
        if (vin.Length == 17)
        {
            vin7 = vin.Substring(10, 7);
            vinType = vin.Substring(3, 4);
        }
        else if (vin.Length == 7)
        {
            vin7 = vin;
        }
        else
        {
            return null;
        }

        using var s = _open("ista/vin/" + char.ToLowerInvariant(vin7[0]) + ".bin");
        return s is null ? null : VinRangeTable.Find(s, vin7, vinType);
    }

    private Dictionary<string, IstaLabel> Labels
    {
        get
        {
            lock (_lock)
            {
                return _labels ??= LoadLabels();
            }
        }
    }

    private Dictionary<string, IstaLabel> LoadLabels()
    {
        var map = new Dictionary<string, IstaLabel>(StringComparer.Ordinal);
        using var s = _open("ista/labels.tsv");
        if (s is null)
        {
            return map;
        }

        using var r = new StreamReader(s, Encoding.UTF8);
        while (r.ReadLine() is { } line)
        {
            var c = line.Split('\t');
            if (c.Length < 5)
            {
                continue;
            }

            map[c[1]] = new IstaLabel(
                c[0] switch { "M" => IstaLabelKind.FaultMode, "E" => IstaLabelKind.Environment, _ => IstaLabelKind.Fault },
                c[1], c[2], SgbdPack.Unescape(c[3]), SgbdPack.Unescape(c[4]),
                c.Length > 5 ? SgbdPack.Unescape(c[5]) : string.Empty,
                c.Length > 6 ? SgbdPack.Unescape(c[6]) : string.Empty);
        }

        return map;
    }

    private Dictionary<string, IReadOnlyDictionary<string, string>> LoadTypeKeys()
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        using var s = _open("ista/typekeys.tsv");
        if (s is null)
        {
            return map;
        }

        using var r = new StreamReader(s, Encoding.UTF8);
        var header = r.ReadLine()?.Split('\t');
        if (header is null)
        {
            return map;
        }

        while (r.ReadLine() is { } line)
        {
            var c = line.Split('\t');
            if (c.Length == 0 || c[0].Length == 0)
            {
                continue;
            }

            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 1; i < c.Length && i < header.Length; i++)
            {
                if (c[i].Length > 0)
                {
                    row[header[i]] = c[i];
                }
            }

            map[c[0]] = row;
        }

        return map;
    }
}

public sealed class IstaManifest
{
    [JsonPropertyName("dbVersion")] public string DatabaseVersion { get; set; } = string.Empty;
    [JsonPropertyName("dbDate")] public string DatabaseDate { get; set; } = string.Empty;
    [JsonPropertyName("ecus")] public int EcuCount { get; set; }
    [JsonPropertyName("faults")] public int FaultCount { get; set; }
    [JsonPropertyName("labels")] public int LabelCount { get; set; }
    [JsonPropertyName("functions")] public int FunctionCount { get; set; }
    [JsonPropertyName("typeKeys")] public int TypeKeyCount { get; set; }
    [JsonPropertyName("vinRanges")] public long VinRangeCount { get; set; }
}

public enum IstaLabelKind { Fault, FaultMode, Environment }

/// <summary>An ISTA text: a fault (code + SAE P-code), a fault mode, or a freeze-frame value (with unit and EDIABAS name).</summary>
public sealed record IstaLabel(IstaLabelKind Kind, string Id, string Code, string SaeCode, string Title, string Unit, string IdentName);

public sealed class IstaEcu
{
    [JsonPropertyName("n")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("g")] public string Group { get; set; } = string.Empty;
    [JsonPropertyName("t")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("f")] public List<IstaFault> Faults { get; set; } = [];
    [JsonPropertyName("fn")] public List<IstaFunction> Functions { get; set; } = [];

    private Dictionary<long, IstaFault>? _byCode;

    public IstaFault? FaultByCode(long code)
    {
        _byCode ??= Faults
            .Select(f => (Code: long.TryParse(f.Code, out var c) ? c : -1, Fault: f))
            .Where(x => x.Code >= 0)
            .GroupBy(x => x.Code)
            .ToDictionary(g => g.Key, g => g.First().Fault);
        return _byCode.TryGetValue(code, out var f) ? f : null;
    }
}

public sealed class IstaFault
{
    [JsonPropertyName("c")] public string Code { get; set; } = string.Empty;
    /// <summary>Fault (F), info (I) or other memory type.</summary>
    [JsonPropertyName("d")] public string DataType { get; set; } = string.Empty;
    [JsonPropertyName("l")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("m")] public List<string> Modes { get; set; } = [];
    [JsonPropertyName("e")] public List<string> Environment { get; set; } = [];
}

/// <summary>One entry of ISTA's function tree for the ECU: a live value, actuator test or identification read.</summary>
public sealed class IstaFunction
{
    /// <summary>Menu path, e.g. "Status values / Fuel system".</summary>
    [JsonPropertyName("p")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("t")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("pre")] public string? Before { get; set; }
    [JsonPropertyName("proc")] public string? During { get; set; }
    [JsonPropertyName("post")] public string? After { get; set; }
    [JsonPropertyName("ms")] public string? DurationMs { get; set; }
    [JsonPropertyName("j")] public List<IstaJob> Jobs { get; set; } = [];
}

public sealed class IstaJob
{
    [JsonPropertyName("n")] public string Name { get; set; } = string.Empty;
    /// <summary>EDIABAS arguments joined with ';'.</summary>
    [JsonPropertyName("a")] public string Arguments { get; set; } = string.Empty;
    [JsonPropertyName("ph")] public string? Phase { get; set; }
    [JsonPropertyName("r")] public List<IstaResult> Results { get; set; } = [];
}

public sealed class IstaResult
{
    [JsonPropertyName("n")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("t")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("u")] public string? Unit { get; set; }
    [JsonPropertyName("mul")] public string? Multiplier { get; set; }
    [JsonPropertyName("off")] public string? Offset { get; set; }
    [JsonPropertyName("rnd")] public string? Round { get; set; }
    [JsonPropertyName("fmt")] public string? Format { get; set; }
    /// <summary>Value to English text, e.g. "0" = "closed".</summary>
    [JsonPropertyName("s")] public Dictionary<string, string>? States { get; set; }
}

/// <summary>A VIN production range: type key, production date and gearbox for every VIN serial in it.</summary>
public sealed record VinRange(string From, string To, string VinTypeKey, string TypeKey, int Year, int Month, string GearBox);

/// <summary>
/// Binary VIN range table for one band (first character of the short VIN), sorted by range start.
/// Layout: "BVR1", int32 count, string table (int32 n, then n × [uint16 len, UTF-8]), then count records of
/// 7-byte From, 7-byte To, uint16 vin-type index, uint16 type-key index, uint16 year, byte month, byte gearbox.
/// </summary>
public static class VinRangeTable
{
    public const int RecordSize = 7 + 7 + 2 + 2 + 2 + 1 + 1;
    private static readonly byte[] s_magic = "BVR1"u8.ToArray();
    private static readonly string[] s_gearBoxes = ["", "A", "M", "?"];

    public static void Write(Stream output, IReadOnlyList<VinRange> sorted)
    {
        var strings = new List<string>();
        var index = new Dictionary<string, ushort>(StringComparer.Ordinal);
        ushort Id(string s)
        {
            if (!index.TryGetValue(s, out var i))
            {
                i = checked((ushort)strings.Count);
                index[s] = i;
                strings.Add(s);
            }

            return i;
        }

        foreach (var r in sorted)
        {
            Id(r.VinTypeKey);
            Id(r.TypeKey);
        }

        using var w = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        w.Write(s_magic);
        w.Write(sorted.Count);
        w.Write(strings.Count);
        foreach (var s in strings)
        {
            var b = Encoding.UTF8.GetBytes(s);
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        Span<byte> rec = stackalloc byte[RecordSize];
        foreach (var r in sorted)
        {
            rec.Clear();
            Encoding.ASCII.GetBytes(r.From.PadRight(7).AsSpan(0, 7), rec[..7]);
            Encoding.ASCII.GetBytes(r.To.PadRight(7).AsSpan(0, 7), rec[7..14]);
            BitConverter.TryWriteBytes(rec[14..16], index[r.VinTypeKey]);
            BitConverter.TryWriteBytes(rec[16..18], index[r.TypeKey]);
            BitConverter.TryWriteBytes(rec[18..20], (ushort)Math.Clamp(r.Year, 0, ushort.MaxValue));
            rec[20] = (byte)Math.Clamp(r.Month, 0, 255);
            rec[21] = (byte)(r.GearBox switch { "A" => 1, "M" => 2, "" => 0, _ => 3 });
            w.Write(rec);
        }
    }

    /// <summary>
    /// Same matching rules as EdiabasLib: the short VIN must lie inside the range in both ASCII and EBCDIC
    /// order, and the VIN's own positions 4–7 must match the range's VIN type when known.
    /// </summary>
    public static VinRange? Find(Stream input, string vin7, string? vinType)
    {
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        var data = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        if (data.Length < 12 || !data[..4].SequenceEqual(s_magic))
        {
            return null;
        }

        var count = BitConverter.ToInt32(data[4..8]);
        var n = BitConverter.ToInt32(data[8..12]);
        var pos = 12;
        var strings = new string[n];
        for (var i = 0; i < n; i++)
        {
            var len = BitConverter.ToUInt16(data[pos..(pos + 2)]);
            pos += 2;
            strings[i] = Encoding.UTF8.GetString(data.Slice(pos, len));
            pos += len;
        }

        var records = data[pos..];
        if (records.Length < (long)count * RecordSize)
        {
            return null;
        }

        Span<byte> key = stackalloc byte[7];
        Encoding.ASCII.GetBytes(vin7, key);

        // Last record whose From <= vin7 (ASCII order).
        int lo = 0, hi = count - 1, last = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (records.Slice(mid * RecordSize, 7).SequenceCompareTo(key) <= 0)
            {
                last = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Ranges can overlap: walk back over every earlier start, newest first. The From/To fields are
        // fixed-width so a long range is found as long as it starts within the scan window.
        VinRange? fallback = null;
        for (var i = last; i >= 0 && last - i < 200_000; i--)
        {
            var rec = records.Slice(i * RecordSize, RecordSize);
            if (rec[7..14].SequenceCompareTo(key) < 0)
            {
                continue;
            }

            var from = Encoding.ASCII.GetString(rec[..7]);
            var to = Encoding.ASCII.GetString(rec[7..14]);
            if (Ebcdic(from, vin7) > 0 || Ebcdic(to, vin7) < 0)
            {
                continue;
            }

            var range = new VinRange(
                from, to,
                strings[BitConverter.ToUInt16(rec[14..16])],
                strings[BitConverter.ToUInt16(rec[16..18])],
                BitConverter.ToUInt16(rec[18..20]),
                rec[20],
                s_gearBoxes[Math.Min((int)rec[21], 3)]);

            if (vinType is null || range.VinTypeKey.Equals(vinType, StringComparison.OrdinalIgnoreCase))
            {
                return range;
            }

            fallback ??= range;
        }

        return vinType is null ? fallback : null;
    }

    /// <summary>EBCDIC collation for VIN characters: letters sort before digits.</summary>
    internal static int Ebcdic(string a, string b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var d = Rank(a[i]) - Rank(b[i]);
            if (d != 0)
            {
                return d;
            }
        }

        return a.Length - b.Length;

        static int Rank(char c) => c switch
        {
            >= 'a' and <= 'z' => c - 'a',
            >= 'A' and <= 'Z' => 100 + (c - 'A'),
            >= '0' and <= '9' => 200 + (c - '0'),
            _ => c,
        };
    }
}
