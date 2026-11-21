using System.IO.Compression;
using System.Xml.Linq;

namespace BmwDecode.Core.Catalog;

/// <summary>One control unit slot of a chassis: diagnostic address plus the group file(s) that identify it.</summary>
public sealed record EcuDefinition(int Address, string Abbreviation, IReadOnlyList<string> Groups)
{
    public string AddressHex => Address >= 0 ? $"0x{Address:X2}" : "—";

    /// <summary>Group file tried first (D_MOTOR, D_CAS, ...).</summary>
    public string PrimaryGroup => Groups[0];

    public string Description => EcuNames.Describe(Abbreviation);

    /// <summary>Stable key used for de-duplication and settings.</summary>
    public string Key => $"{Address:X2}:{PrimaryGroup}";
}

/// <summary>A chassis (Baureihe) such as E84 with all ECUs it can carry.</summary>
public sealed record SeriesDefinition(string Series, string Family, string BusType, IReadOnlyList<EcuDefinition> Ecus)
{
    public bool IsESeries => Series.StartsWith('E') && !BusType.Contains("MOTORBIKE", StringComparison.OrdinalIgnoreCase);

    /// <summary>K-line / DS2 generation (E30 … E53, E83, E85): identified via the ZCS coding key rather than CAS/FA.</summary>
    public bool IsDs2 => BusType is "IBUS" or "KBUS" or "DS2";

    /// <summary>Generic profile for a chassis the ISTA catalogue does not list (all DS2 modules are probed).</summary>
    public bool IsGeneric { get; init; }

    /// <summary>ZCS SGBD that reads the coding key on DS2 cars (ZCS_E36, ZCS_E39, ZCS_ALL...).</summary>
    public string? IdentSgbd => Family.StartsWith("ZCS", StringComparison.OrdinalIgnoreCase) ? Family : IsDs2 ? "ZCS_ALL" : null;

    public string Generation => IsDs2 ? "DS2 / K-line" : BusType == "BN2000" ? "BN2000 / D-CAN" : BusType;

    public string DisplayName => SeriesNames.Describe(Series) + (IsGeneric ? " – generic" : string.Empty);
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

        AddClassicSeries(result);
        return new VehicleCatalog(result);
    }

    /// <summary>
    /// Pre-1995 cars (E30, E31, E32, E34) and the E36/7 Z3 are not in the ISTA-derived catalogue. They speak the same
    /// DS2 protocol on K-line, so they get a generic profile that probes every DS2 module of E36/E38/E39 (like INPA's
    /// BMW_ALT menu). Modules that are not fitted simply do not answer.
    /// </summary>
    private static void AddClassicSeries(List<SeriesDefinition> series)
    {
        var donors = series.Where(s => s.Series is "E36" or "E38" or "E39").ToList();
        if (donors.Count == 0)
        {
            return;
        }

        var ecus = donors.SelectMany(s => s.Ecus)
            .GroupBy(e => e.PrimaryGroup, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(e => e.Address)
            .ToList();

        foreach (var classic in new[] { "E30", "E31", "E32", "E34", "E36/7" })
        {
            if (series.All(s => !s.Series.Equals(classic, StringComparison.OrdinalIgnoreCase)))
            {
                series.Add(new SeriesDefinition(classic, "ZCS_ALL", "DS2", ecus) { IsGeneric = true });
            }
        }
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
