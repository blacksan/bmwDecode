#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BmwDecode.Core.Data;

namespace BmwDecode.DataBuilder;

/// <summary>
/// Imports an ECU functions export (EcuFunctions.zip or its unzipped folder) into the ista/ section of the
/// pack: English fault / fault-mode / freeze-frame labels, each ECU variant's fault list and labelled
/// functions, type keys and VIN ranges. Reads the export as-is; it never touches ISTA's own databases.
/// </summary>
public sealed class IstaImporter : IDisposable
{
    private readonly ZipArchive? _zip;
    private readonly string? _folder;

    public IstaImporter(string source)
    {
        if (Directory.Exists(source))
        {
            _folder = source;
        }
        else
        {
            _zip = ZipFile.OpenRead(source);
        }
    }

    public void Dispose() => _zip?.Dispose();

    private IEnumerable<string> Names =>
        _zip is not null
            ? _zip.Entries.Where(e => e.Length > 0).Select(e => e.FullName)
            : Directory.EnumerateFiles(_folder!).Select(Path.GetFileName).Cast<string>();

    private Stream? Open(string name)
    {
        if (_zip is not null)
        {
            return _zip.GetEntry(name)?.Open();
        }

        var p = Path.Combine(_folder!, name);
        return File.Exists(p) ? File.OpenRead(p) : null;
    }

    public void ImportInto(SgbdPackWriter writer, Action<string> log)
    {
        var manifest = new IstaManifest();

        // 1. Fault dictionary (English).
        var labelCount = 0;
        writer.AddEntry("ista/labels.tsv", s =>
        {
            using var w = new StreamWriter(s, new UTF8Encoding(false));
            using var fs = Open("faultdata_en.xml") ?? throw new FileNotFoundException("faultdata_en.xml not in export");
            using var xr = XmlReader.Create(fs, new XmlReaderSettings { IgnoreWhitespace = true });
            // XNode.ReadFrom leaves the reader on the next node already, so only call Read() when nothing was consumed.
            xr.Read();
            while (!xr.EOF)
            {
                if (xr.NodeType != XmlNodeType.Element)
                {
                    xr.Read();
                    continue;
                }

                if (xr.Name is "DbVer")
                {
                    manifest.DatabaseVersion = xr.ReadElementContentAsString();
                    continue;
                }

                if (xr.Name is "DbDat")
                {
                    manifest.DatabaseDate = xr.ReadElementContentAsString();
                    continue;
                }

                if (xr.Name is not ("FCLab" or "FMLab" or "ECLab"))
                {
                    xr.Read();
                    continue;
                }

                var kind = xr.Name switch { "FMLab" => "M", "ECLab" => "E", _ => "C" };
                var e = (XElement)XNode.ReadFrom(xr);
                var title = Text(e.Element("Tit"));
                w.Write(kind);
                w.Write('\t');
                w.Write(e.Element("Id")?.Value);
                w.Write('\t');
                w.Write(e.Element("Cod")?.Value);
                w.Write('\t');
                w.Write(SgbdPack.Escape(e.Element("Sae")?.Value ?? string.Empty));
                w.Write('\t');
                w.Write(SgbdPack.Escape(title));
                w.Write('\t');
                w.Write(SgbdPack.Escape(e.Element("Uni")?.Value ?? string.Empty));
                w.Write('\t');
                w.Write(SgbdPack.Escape(e.Element("IStr")?.Value ?? string.Empty));
                w.Write('\n');
                labelCount++;
            }
        });
        manifest.LabelCount = labelCount;
        log($"  {labelCount} fault labels (ISTA database {manifest.DatabaseVersion})");

        // 2. Each ECU variant: fault list and labelled functions.
        var ecuFiles = Names
            .Where(n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                        && !n.StartsWith("faultdata_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var file in ecuFiles)
        {
            IstaEcu ecu;
            try
            {
                using var s = Open(file)!;
                ecu = ParseEcu(Path.GetFileNameWithoutExtension(file).ToUpperInvariant(), XDocument.Load(s).Root!);
            }
            catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
            {
                log($"  skipped {file}: {ex.Message}");
                continue;
            }

            writer.AddJson("ista/ecu/" + ecu.Name + ".json", ecu);
            manifest.EcuCount++;
            manifest.FaultCount += ecu.Faults.Count;
            manifest.FunctionCount += ecu.Functions.Count;
        }

        log($"  {manifest.EcuCount} ECU variants, {manifest.FaultCount} fault codes, {manifest.FunctionCount} functions");

        // 3. Type keys.
        writer.AddEntry("ista/typekeys.tsv", s =>
        {
            using var w = new StreamWriter(s, new UTF8Encoding(false));
            using var r = new StreamReader(Open("typekeyinfo.txt") ?? throw new FileNotFoundException("typekeyinfo.txt"), Encoding.UTF8);
            string[]? names = null;
            while (r.ReadLine() is { } line)
            {
                if (line.StartsWith('#'))
                {
                    names = line.TrimStart('#').Split('|');
                    // Column 0 of each row is the key; the remaining values line up with the header names.
                    w.Write("TypeKey");
                    foreach (var n in names)
                    {
                        w.Write('\t');
                        w.Write(TypeKeyColumn(n));
                    }

                    w.Write('\n');
                    continue;
                }

                var c = line.Split('|');
                if (names is null || c.Length < 2 || c[0].Trim().Length == 0)
                {
                    continue;
                }

                w.Write(c[0].Trim());
                for (var i = 1; i <= names.Length && i < c.Length; i++)
                {
                    w.Write('\t');
                    w.Write(c[i].Trim().Replace('\t', ' '));
                }

                w.Write('\n');
                manifest.TypeKeyCount++;
            }
        });
        log($"  {manifest.TypeKeyCount} type keys");

        // 4. VIN ranges, one binary table per band (first character of the short VIN).
        foreach (var file in Names.Where(n => n.StartsWith("vinranges_", StringComparison.OrdinalIgnoreCase)
                                             && n.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n))
        {
            var band = Path.GetFileNameWithoutExtension(file)["vinranges_".Length..].ToLowerInvariant();
            var ranges = new List<VinRange>();
            using (var r = new StreamReader(Open(file)!, Encoding.UTF8))
            {
                while (r.ReadLine() is { } line)
                {
                    if (line.StartsWith('#'))
                    {
                        continue;
                    }

                    var c = line.Split(',');
                    if (c.Length < 4 || c[0].Length != 7 || c[1].Length != 7)
                    {
                        continue;
                    }

                    ranges.Add(new VinRange(
                        c[0].ToUpperInvariant(), c[1].ToUpperInvariant(), c[2], c[3],
                        c.Length > 4 && int.TryParse(c[4], NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : 0,
                        c.Length > 5 && int.TryParse(c[5], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0,
                        c.Length > 7 ? c[7] : string.Empty));
                }
            }

            ranges.Sort((a, b) => string.CompareOrdinal(a.From, b.From));
            writer.AddEntry("ista/vin/" + band + ".bin", s => VinRangeTable.Write(s, ranges), CompressionLevel.SmallestSize);
            manifest.VinRangeCount += ranges.Count;
        }

        log($"  {manifest.VinRangeCount} VIN ranges");
        writer.AddJson("ista/manifest.json", manifest);
    }

    internal static IstaEcu ParseEcu(string name, XElement root)
    {
        var ecu = new IstaEcu
        {
            Name = name,
            Group = root.Element("GNam")?.Value ?? string.Empty,
            Title = Text(root.Element("Tit")),
        };

        foreach (var f in root.Element("FCL")?.Elements("FCod") ?? [])
        {
            ecu.Faults.Add(new IstaFault
            {
                Code = f.Element("Cod")?.Value ?? string.Empty,
                DataType = f.Element("DTyp")?.Value ?? string.Empty,
                Label = f.Element("FCLIdL")?.Value ?? string.Empty,
                Modes = f.Element("FMLIdL")?.Elements("Str").Select(x => x.Value).ToList() ?? [],
                Environment = f.Element("ECLIdL")?.Elements("Str").Select(x => x.Value).ToList() ?? [],
            });
        }

        // Function tree: FSL/FStr (menu nodes, PId = parent) -> FFSL/FFStr (functions) -> JL/Job -> JRL/JRes.
        var nodes = root.Element("FSL")?.Elements("FStr").ToList() ?? [];
        var byId = nodes.Where(n => n.Element("Id") is not null)
            .GroupBy(n => n.Element("Id")!.Value).ToDictionary(g => g.Key, g => g.First());
        string PathOf(XElement node)
        {
            var parts = new List<string>();
            var seen = new HashSet<string>();
            for (var n = node; n is not null;)
            {
                var title = Text(n.Element("Tit")).Trim('-', ' ');
                if (title.Length > 0)
                {
                    parts.Insert(0, title);
                }

                var pid = n.Element("PId")?.Value;
                if (pid is null || !seen.Add(pid) || !byId.TryGetValue(pid, out n))
                {
                    break;
                }
            }

            return string.Join(" / ", parts);
        }

        foreach (var node in nodes)
        {
            var path = PathOf(node);
            foreach (var ffs in node.Element("FFSL")?.Elements("FFStr") ?? [])
            {
                ecu.Functions.Add(ParseFunction(ffs, path));
            }
        }

        foreach (var ffs in root.Element("RVL")?.Elements("RVar").SelectMany(v => v.Element("FFSL")?.Elements("FFStr") ?? []) ?? [])
        {
            ecu.Functions.Add(ParseFunction(ffs, "Identification"));
        }

        return ecu;
    }

    private static IstaFunction ParseFunction(XElement ffs, string path)
    {
        var fn = new IstaFunction
        {
            Path = path,
            Title = Text(ffs.Element("Tit")),
            Before = NullIfEmpty(Text(ffs.Element("Pre"))),
            During = NullIfEmpty(Text(ffs.Element("Proc"))),
            After = NullIfEmpty(Text(ffs.Element("Post"))),
            DurationMs = NullIfEmpty(ffs.Element("ActDur")?.Value),
        };

        foreach (var job in ffs.Element("JL")?.Elements("Job") ?? [])
        {
            var j = new IstaJob
            {
                Name = job.Element("Nam")?.Value ?? string.Empty,
                Phase = NullIfEmpty(job.Element("Pha")?.Value),
                Arguments = string.Join(";", job.Element("JPL")?.Elements("JPar").Select(p => p.Element("Val")?.Value ?? string.Empty) ?? []),
            };
            foreach (var res in job.Element("JRL")?.Elements("JRes") ?? [])
            {
                var states = res.Element("RSVL")?.Elements("RSVal")
                    .Select(v => (Value: v.Element("SVal")?.Value, Text: Text(v.Element("Tit"))))
                    .Where(v => !string.IsNullOrEmpty(v.Value) && v.Text.Length > 0)
                    .GroupBy(v => v.Value!)
                    .ToDictionary(g => g.Key, g => g.First().Text);
                j.Results.Add(new IstaResult
                {
                    Name = res.Element("Nam")?.Value ?? string.Empty,
                    Title = Text(res.Element("Tit")),
                    Unit = NullIfEmpty(res.Element("Uni")?.Value),
                    Multiplier = NullIfEmpty(res.Element("Mul")?.Value),
                    Offset = NullIfEmpty(res.Element("Off")?.Value),
                    Round = NullIfEmpty(res.Element("Rnd")?.Value),
                    Format = NullIfEmpty(res.Element("Form")?.Value),
                    States = states is { Count: > 0 } ? states : null,
                });
            }

            fn.Jobs.Add(j);
        }

        return fn;
    }

    /// <summary>English text of an ISTA translation element, German when English is missing.</summary>
    private static string Text(XElement? tit) =>
        (tit?.Element("En")?.Value is { Length: > 0 } en ? en : tit?.Element("De")?.Value ?? string.Empty).Trim();

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>typekeyinfo.txt column names (German, umlauts already stripped to '?') in English.</summary>
    internal static string TypeKeyColumn(string german)
    {
        var key = new string(german.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return key switch
        {
            "typschlssel" or "typschlüssel" => "TypeKeyName",
            "grundtyp" => "BaseType",
            "produktart" => "ProductType",
            "baureihe" => "SeriesCode",
            "ebezeichnung" => "Series",
            "getriebe" => "Gearbox",
            "motorleistungsklasse" => "PowerClass",
            "lenkung" => "Steering",
            "motor" => "Engine",
            "motorhubraum" => "Displacement",
            "motorkraftstoffarteinbaulage" => "Fuel",
            "verkaufsbezeichnung" => "Model",
            "antrieb" => "Drive",
            "tren" or "türen" => "Doors",
            "marke" => "Brand",
            "sicherheitsfahrzeug" => "Armoured",
            "motorberarbeitung" or "motorüberarbeitung" => "EngineRevision",
            "karosserie" => "Body",
            "lnderausfhrung" or "länderausführung" => "Market",
            "produktlinie" => "ProductLine",
            "hybridkennzeichen" => "Hybrid",
            "sportausfhrung" or "sportausführung" => "Sport",
            "aebezeichnung" => "AeName",
            "basisausfhrung" or "basisausführung" => "BaseVersion",
            "montageland" => "AssemblyCountry",
            "elektrischereichweite" => "ElectricRange",
            "lebenszyklus" => "LifeCycle",
            "elebenszyklus" => "ElectricLifeCycle",
            "emaschineeinbaulage" => "ElectricMotorPosition",
            "emaschinekraftstoffart" => "ElectricMotorFuel",
            _ => german.Trim(),
        };
    }
}
