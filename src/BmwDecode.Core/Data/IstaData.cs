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
    private readonly ConcurrentDictionary<string, Lazy<IstaEcu?>> _ecus = new(StringComparer.OrdinalIgnoreCase);
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
        _ecus.GetOrAdd(variant, v => new Lazy<IstaEcu?>(() =>
        {
            using var s = _open("ista/ecu/" + v.ToUpperInvariant() + ".json");
            return s is null ? null : System.Text.Json.JsonSerializer.Deserialize<IstaEcu>(s, SgbdPack.Json);
        })).Value;

    public IstaLabel? Label(string? id) =>
        id is not null && Labels.TryGetValue(id, out var l) ? l : null;

    /// <summary>English text for a fault code (F_ORT_NR) of an ECU variant — fault memory, or info memory — or null.</summary>
    public string? FaultText(string variant, long code, bool infoMemory = false)
    {
        var ecu = Ecu(variant);
        var fault = ecu?.FaultByCode(code, infoMemory);
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

        var band = Band(char.ToLowerInvariant(vin7[0]));
        return band is null ? null : VinRangeTable.Find(band, vin7, vinType);
    }

    // The last band used stays cached (bands are up to ~100 MB raw); the GC may reclaim it under memory pressure.
    private readonly object _bandLock = new();
    private char _bandName;
    private WeakReference<VinRangeTable.Band>? _band;

    private VinRangeTable.Band? Band(char name)
    {
        lock (_bandLock)
        {
            if (_bandName == name && _band is not null && _band.TryGetTarget(out var cached))
            {
                return cached;
            }

            using var s = _open("ista/vin/" + name + ".bin");
            if (s is not MemoryStream ms || !ms.TryGetBuffer(out var buffer) || buffer.Array is null)
            {
                return null;
            }

            var band = VinRangeTable.Load(buffer.Array, (int)ms.Length);
            if (band is not null)
            {
                _bandName = name;
                _band = new WeakReference<VinRangeTable.Band>(band);
            }

            return band;
        }
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

    private Dictionary<(bool Info, long Code), IstaFault>? _byCode;

    /// <summary>
    /// The fault for a code in fault memory (data type F) or info memory (I); the same code can mean different
    /// things in the two, as in EdiabasLib's EcuFunctionReader.
    /// </summary>
    public IstaFault? FaultByCode(long code, bool infoMemory = false)
    {
        var map = LazyInitializer.EnsureInitialized(ref _byCode, () => Faults
            .Select(f => (Code: long.TryParse(f.Code, out var c) ? c : -1, Fault: f))
            .Where(x => x.Code >= 0)
            .GroupBy(x => (Info: x.Fault.DataType.Equals("I", StringComparison.OrdinalIgnoreCase), x.Code))
            .ToDictionary(g => g.Key, g => g.First().Fault));
        return map.TryGetValue((infoMemory, code), out var f) ? f : null;
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
/// Binary VIN range table for one band (first character of the short VIN).
/// Layout: "BVR2", int32 count, string table (int32 n, then n × [uint16 len, UTF-8]), then count records sorted
/// (stably) by range start: 7-byte From, 7-byte To, uint16 vin-type index, uint16 type-key index, uint16 year,
/// byte month, byte gearbox, int32 position in the original vinranges file (EdiabasLib picks by file order).
/// </summary>
public static class VinRangeTable
{
    public const int RecordSize = 7 + 7 + 2 + 2 + 2 + 1 + 1 + 4;
    private static readonly byte[] s_magic = "BVR2"u8.ToArray();
    private static readonly string[] s_gearBoxes = ["", "A", "M", "?"];

    /// <summary>Writes ranges given in file order.</summary>
    public static void Write(Stream output, IReadOnlyList<VinRange> fileOrder)
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

        foreach (var r in fileOrder)
        {
            Id(r.VinTypeKey);
            Id(r.TypeKey);
        }

        // OrderBy is stable: equal starts keep file order.
        var sorted = Enumerable.Range(0, fileOrder.Count)
            .OrderBy(i => fileOrder[i].From, StringComparer.Ordinal)
            .ToList();

        using var w = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        w.Write(s_magic);
        w.Write(fileOrder.Count);
        w.Write(strings.Count);
        foreach (var s in strings)
        {
            var b = Encoding.UTF8.GetBytes(s);
            w.Write((ushort)b.Length);
            w.Write(b);
        }

        Span<byte> rec = stackalloc byte[RecordSize];
        foreach (var i in sorted)
        {
            var r = fileOrder[i];
            rec.Clear();
            Encoding.ASCII.GetBytes(r.From.PadRight(7).AsSpan(0, 7), rec[..7]);
            Encoding.ASCII.GetBytes(r.To.PadRight(7).AsSpan(0, 7), rec[7..14]);
            BitConverter.TryWriteBytes(rec[14..16], index[r.VinTypeKey]);
            BitConverter.TryWriteBytes(rec[16..18], index[r.TypeKey]);
            BitConverter.TryWriteBytes(rec[18..20], (ushort)Math.Clamp(r.Year, 0, ushort.MaxValue));
            rec[20] = (byte)Math.Clamp(r.Month, 0, 255);
            rec[21] = (byte)(r.GearBox switch { "A" => 1, "M" => 2, "" => 0, _ => 3 });
            BitConverter.TryWriteBytes(rec[22..26], i);
            w.Write(rec);
        }
    }

    /// <summary>A loaded band: raw records plus, per record, the index of the latest-ending range so far.</summary>
    public sealed class Band
    {
        internal Band(byte[] data, int recordsOffset, int count, string[] strings)
        {
            Data = data;
            RecordsOffset = recordsOffset;
            Count = count;
            Strings = strings;
            MaxToUpTo = new int[count];
            var best = -1;
            for (var i = 0; i < count; i++)
            {
                if (best < 0 || To(i).SequenceCompareTo(To(best)) > 0)
                {
                    best = i;
                }

                MaxToUpTo[i] = best;
            }
        }

        internal byte[] Data { get; }
        internal int RecordsOffset { get; }
        public int Count { get; }
        internal string[] Strings { get; }

        /// <summary>MaxToUpTo[i] = record among 0..i with the highest To: lets the scan stop exactly.</summary>
        internal int[] MaxToUpTo { get; }

        internal ReadOnlySpan<byte> Record(int i) => Data.AsSpan(RecordsOffset + i * RecordSize, RecordSize);

        internal ReadOnlySpan<byte> From(int i) => Record(i)[..7];

        internal ReadOnlySpan<byte> To(int i) => Record(i)[7..14];
    }

    /// <summary>Parses a band file; null when it is not a valid BVR2 table.</summary>
    public static Band? Load(byte[] data, int length)
    {
        try
        {
            if (length < 12 || !data.AsSpan(0, 4).SequenceEqual(s_magic))
            {
                return null;
            }

            var count = BitConverter.ToInt32(data, 4);
            var n = BitConverter.ToInt32(data, 8);
            if (count < 0 || n < 0 || n > ushort.MaxValue + 1)
            {
                return null;
            }

            var pos = 12;
            var strings = new string[n];
            for (var i = 0; i < n; i++)
            {
                var len = BitConverter.ToUInt16(data, pos);
                pos += 2;
                if (pos + len > length)
                {
                    return null;
                }

                strings[i] = Encoding.UTF8.GetString(data, pos, len);
                pos += len;
            }

            return length - pos < (long)count * RecordSize ? null : new Band(data, pos, count, strings);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static VinRange? Find(Stream input, string vin7, string? vinType)
    {
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        var band = Load(ms.GetBuffer(), (int)ms.Length);
        return band is null ? null : Find(band, vin7, vinType);
    }

    /// <summary>
    /// EdiabasLib's rules (VehicleInfoBmw.GetRangeInfoFromVin): a range matches when the short VIN lies inside it in
    /// both ASCII and EBCDIC order and, for a full VIN, the range's VIN type equals VIN positions 4–7. An exact
    /// single-VIN range wins outright. Otherwise several matches with a known VIN type are ambiguous (null); with
    /// only a short VIN the first match in file order is used, without VIN type and gearbox.
    /// </summary>
    public static VinRange? Find(Band band, string vin7, string? vinType)
    {
        vin7 = vin7.ToUpperInvariant();
        if (vin7.Length != 7)
        {
            return null;
        }

        Span<byte> key = stackalloc byte[7];
        Encoding.ASCII.GetBytes(vin7, key);

        // Last record whose From <= vin7.
        int lo = 0, hi = band.Count - 1, last = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            if (band.From(mid).SequenceCompareTo(key) <= 0)
            {
                last = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        var matches = new List<(int Order, VinRange Range)>();
        for (var i = last; i >= 0; i--)
        {
            // Nothing at or before i ends at or after the VIN: no earlier range can contain it.
            if (band.To(band.MaxToUpTo[i]).SequenceCompareTo(key) < 0)
            {
                break;
            }

            if (band.To(i).SequenceCompareTo(key) < 0)
            {
                continue;
            }

            var range = Read(band, i, out var order);
            if (vinType is not null && !range.VinTypeKey.Equals(vinType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Ebcdic(range.From, vin7) > 0 || Ebcdic(range.To, vin7) < 0)
            {
                continue;
            }

            matches.Add((order, range));
        }

        if (matches.Count == 0)
        {
            return null;
        }

        matches.Sort((a, b) => a.Order.CompareTo(b.Order));
        var exact = matches.FirstOrDefault(m => m.Range.From == vin7 && m.Range.To == vin7);
        if (exact.Range is not null)
        {
            return exact.Range;
        }

        if (matches.Count == 1)
        {
            return matches[0].Range;
        }

        return vinType is not null ? null : matches[0].Range with { VinTypeKey = string.Empty, GearBox = string.Empty };
    }

    private static VinRange Read(Band band, int i, out int order)
    {
        var rec = band.Record(i);
        order = BitConverter.ToInt32(rec[22..26]);
        return new VinRange(
            Encoding.ASCII.GetString(rec[..7]),
            Encoding.ASCII.GetString(rec[7..14]),
            band.Strings[BitConverter.ToUInt16(rec[14..16])],
            band.Strings[BitConverter.ToUInt16(rec[16..18])],
            BitConverter.ToUInt16(rec[18..20]),
            rec[20],
            s_gearBoxes[Math.Min((int)rec[21], 3)]);
    }

    /// <summary>EdiabasLib's EbcdicVIN7Comparer for VIN characters: letters sort before digits.</summary>
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

        // Digits sort after everything else; other characters by code (EdiabasLib's EbcdicVIN7Comparer).
        static int Rank(char c) => char.IsDigit(c) ? 0x10000 + c : c;
    }
}
