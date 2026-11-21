using System.IO.Compression;
using System.Xml.Linq;

namespace BmwDecode.Core.Catalog;

/// <summary>One control unit slot of a chassis: diagnostic address plus the group file(s) that identify it.</summary>
public sealed record EcuDefinition(int Address, string Abbreviation, IReadOnlyList<string> Groups)
{
    public string AddressHex => $"0x{Address:X2}";

    /// <summary>Group file tried first (D_MOTOR, D_CAS, ...).</summary>
    public string PrimaryGroup => Groups[0];

    public string Description => EcuNames.Describe(Abbreviation);

    /// <summary>Stable key used for de-duplication and settings.</summary>
    public string Key => $"{Address:X2}:{PrimaryGroup}";
}

/// <summary>A chassis (Baureihe) such as E84 with all ECUs it can carry.</summary>
public sealed record SeriesDefinition(string Series, string Family, string BusType, IReadOnlyList<EcuDefinition> Ecus)
{
    public bool IsESeries => Series.StartsWith('E');

    public string DisplayName => SeriesNames.Describe(Series);
}

/// <summary>
/// Chassis catalogue built from EdiabasLib's VehicleSeries.xml (derived from BMW ISTA data).
/// Maps every chassis to the diagnostic addresses and group SGBDs of its control units.
/// </summary>
public sealed class VehicleCatalog
{
    private readonly Dictionary<string, SeriesDefinition> _series;

    private VehicleCatalog(IEnumerable<SeriesDefinition> series)
    {
        _series = series.ToDictionary(s => s.Series, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<SeriesDefinition> All => _series.Values;

    /// <summary>E-series chassis (E36 ... E93), sorted by name.</summary>
    public IReadOnlyList<SeriesDefinition> ESeries =>
        _series.Values.Where(s => s.IsESeries).OrderBy(s => s.Series, StringComparer.Ordinal).ToList();

    public SeriesDefinition? Find(string? series) =>
        series is not null && _series.TryGetValue(series.Trim(), out var s) ? s : null;

    private static readonly Lazy<VehicleCatalog> s_default = new(LoadEmbedded);

    /// <summary>Catalogue embedded in the application.</summary>
    public static VehicleCatalog Default => s_default.Value;

    private static VehicleCatalog LoadEmbedded()
    {
        var assembly = typeof(VehicleCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream("BmwDecode.Core.VehicleSeries.zip")
            ?? throw new InvalidOperationException("Embedded VehicleSeries.zip is missing.");
        return LoadZip(stream);
    }

    public static VehicleCatalog LoadZip(Stream zipStream)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("VehicleSeries.zip contains no XML.");
        using var xml = entry.Open();
        return LoadXml(xml);
    }

    public static VehicleCatalog LoadXml(Stream xmlStream)
    {
        var doc = XDocument.Load(xmlStream);
        var result = new List<SeriesDefinition>();

        foreach (var item in doc.Descendants("VehicleSeriesDict").Elements("item"))
        {
            var series = item.Element("key")?.Element("string")?.Value?.Trim();
            if (string.IsNullOrEmpty(series))
            {
                continue;
            }

            var variants = item.Descendants("VSI").ToList();
            if (variants.Count == 0)
            {
                continue;
            }

            // Prefer the car variant; merge ECUs of all variants so nothing is missed.
            var main = variants.FirstOrDefault(v => (string?)v.Element("BL") == "BMWPKW") ?? variants[0];
            var family = (string?)main.Element("BS") ?? (string?)main.Element("MS") ?? series;
            var busType = (string?)main.Element("BT") ?? string.Empty;

            var ecus = new Dictionary<string, EcuDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in variants.SelectMany(v => v.Elements("EL")))
            {
                var ecu = ParseEcu(el);
                if (ecu is not null)
                {
                    ecus.TryAdd(ecu.Key, ecu);
                }
            }

            result.Add(new SeriesDefinition(series, family, busType,
                ecus.Values.OrderBy(e => e.Address).ThenBy(e => e.Abbreviation, StringComparer.Ordinal).ToList()));
        }

        return new VehicleCatalog(result);
    }

    private static EcuDefinition? ParseEcu(XElement el)
    {
        if (!int.TryParse((string?)el.Element("DA"), out var address))
        {
            return null;
        }

        var name = ((string?)el.Element("Nm"))?.Trim() ?? string.Empty;
        var groups = (((string?)el.Element("GS")) ?? string.Empty)
            .Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(g => g.Length > 2 && !g.EndsWith('_')) // "D_" placeholders carry no group file
            .ToList();

        // Virtual ECUs (VIRTSGxx) are gateway-internal bookkeeping, not real modules.
        if (groups.Count == 0 || name.StartsWith("VIRTSG", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new EcuDefinition(address, name, groups);
    }
}
