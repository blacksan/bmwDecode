using System.Globalization;
using System.Xml.Linq;

namespace BmwDecode.Core.Vehicle;

/// <summary>A car ISTA has identified on this PC, read from its plain-XML session files.</summary>
public sealed record IstaVehicleRecord(
    string Vin,
    string? TypeKey,
    string? Series,
    string? Model,
    string? Engine,
    string? EngineCode,
    string? Displacement,
    string? Gearbox,
    string? Drive,
    string? Body,
    string? Market,
    string? Steering,
    DateOnly? Production,
    string? MainSgbd,
    string? BusType,
    DateTimeOffset? SeenAt,
    string SourceFile)
{
    /// <summary>True when this came from BMW's factory VIN ranges in the data pack, not from an ISTA session.</summary>
    public bool IsFactoryRange => SourceFile == Data.ReferenceData.FactoryRangesSource;
}

/// <summary>
/// Reuses what ISTA already decoded: every ISTA session leaves RG_META_*.xml / RG_TRANS_*.xml files (unencrypted)
/// in its Transactions folder with the car's VIN, type key, series, sales name, engine, gearbox and build date.
/// bmwDecode reads those files; it never opens ISTA's encrypted databases.
/// </summary>
public static class IstaHistory
{
    /// <summary>Folders where ISTA+ writes its session files.</summary>
    public static IEnumerable<string> DefaultFolders()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programData, "BMW", "ISPI", "data", "TRIC", "ISTA", "Transactions");
        yield return Path.Combine(programFiles, "BMW", "ISPI", "TRIC", "ISTA", "Transactions");
        yield return @"C:\Rheingold\Transactions";
    }

    public static IReadOnlyList<IstaVehicleRecord> Load(IEnumerable<string>? folders = null)
    {
        var byVin = new Dictionary<string, IstaVehicleRecord>(StringComparer.OrdinalIgnoreCase);
        var all = new List<string>();
        foreach (var folder in (folders ?? DefaultFolders()).Where(Directory.Exists))
        {
            try
            {
                all.AddRange(Directory.EnumerateFiles(folder, "RG_*.xml"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable folder: skip
            }
        }

        // One chronological pass over every folder so the newest session wins.
        foreach (var file in all.OrderBy(File.GetLastWriteTimeUtc))
        {
            {
                IstaVehicleRecord? record;
                try
                {
                    record = Parse(XDocument.Load(file), file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                {
                    continue;
                }

                if (record is null)
                {
                    continue;
                }

                // Newer files win, but keep fields an older file had and the newer one lacks.
                byVin[record.Vin] = byVin.TryGetValue(record.Vin, out var old) ? Combine(record, old) : record;
            }
        }

        return byVin.Values.OrderByDescending(r => r.SeenAt).ToList();
    }

    /// <summary>Parses an RG_META (BasicFeatures) or RG_TRANS (Vehicle) document.</summary>
    internal static IstaVehicleRecord? Parse(XDocument doc, string source)
    {
        var root = doc.Root;
        if (root is null)
        {
            return null;
        }

        string? Val(XElement? scope, string name)
        {
            var v = scope?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();
            return string.IsNullOrEmpty(v) || v is "-" or "UNKNOWN" ? null : v;
        }

        var vin = Val(root, "VIN17");
        if (vin is not { Length: 17 })
        {
            return null;
        }

        if (root.Name.LocalName == "TransactionMetaData")
        {
            var bf = root.Elements().FirstOrDefault(e => e.Name.LocalName == "BasicFeatures");
            if (bf is null)
            {
                return null;
            }

            var date = DateTimeOffset.TryParse(Val(root, "EndDate") ?? Val(root, "StartDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTimeOffset?)null;
            return new IstaVehicleRecord(vin, Val(bf, "TypeCode"), Val(bf, "Ereihe"), Val(bf, "VerkaufsBezeichnung"), Val(bf, "Motor"), null, null,
                Val(bf, "Getriebe"), null, Val(bf, "Karosserie"), Val(bf, "Land"), Val(bf, "Lenkung"),
                ProductionFrom(Val(bf, "Modelljahr"), Val(bf, "Modellmonat")), null, null, date, source);
        }

        if (root.Name.LocalName == "Vehicle")
        {
            var motor = root.Elements().FirstOrDefault(e => e.Name.LocalName == "GenericMotor");
            var hub = Val(root, "Hubraum");
            var displacement = hub is not null && double.TryParse(hub, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && h > 0
                ? $"{h / 10.0:0.0} L"
                : null;
            DateOnly? production = DateTime.TryParse(Val(root, "ProductionDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var p)
                ? DateOnly.FromDateTime(p)
                : ProductionFrom(Val(root, "Modelljahr"), Val(root, "Modellmonat"));
            return new IstaVehicleRecord(vin, Val(root, "Typ"), Val(root, "Ereihe"), null, Val(root, "Motor"), Val(motor, "EngineLabel1"), displacement,
                Val(root, "Getriebe"), Val(root, "Antrieb"), Val(root, "Karosserie"), Val(root, "Land"), Val(root, "Lenkung"), production,
                Val(root, "MainSeriesSgbd"), Val(root, "BNType"), File.Exists(source) ? File.GetLastWriteTime(source) : null, source);
        }

        return null;
    }

    public static TypeKeyInfo ToTypeKeyInfo(IstaVehicleRecord r) => new(
        r.TypeKey!, r.Series, r.Model, r.EngineCode ?? r.Engine, r.Drive, r.Gearbox, r.Displacement, r.Body, null, r.Steering, null, r.Market,
        r.Production?.ToString("MM/yyyy", CultureInfo.InvariantCulture), $"ISTA session ({r.SeenAt:yyyy-MM-dd})");

    private static DateOnly? ProductionFrom(string? year, string? month) =>
        int.TryParse(year, out var y) && int.TryParse(month, out var m) && y > 1980 && m is >= 1 and <= 12 ? new DateOnly(y, m, 1) : null;

    private static IstaVehicleRecord Combine(IstaVehicleRecord n, IstaVehicleRecord o) => n with
    {
        TypeKey = n.TypeKey ?? o.TypeKey,
        Series = n.Series ?? o.Series,
        Model = n.Model ?? o.Model,
        Engine = n.Engine ?? o.Engine,
        EngineCode = n.EngineCode ?? o.EngineCode,
        Displacement = n.Displacement ?? o.Displacement,
        Gearbox = n.Gearbox ?? o.Gearbox,
        Drive = n.Drive ?? o.Drive,
        Body = n.Body ?? o.Body,
        Market = n.Market ?? o.Market,
        Steering = n.Steering ?? o.Steering,
        Production = n.Production ?? o.Production,
        MainSgbd = n.MainSgbd ?? o.MainSgbd,
        BusType = n.BusType ?? o.BusType,
        SeenAt = n.SeenAt ?? o.SeenAt,
    };
}
