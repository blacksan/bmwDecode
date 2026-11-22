using System.IO.Compression;
using System.Text;
using BmwDecode.Core.Data;
using BmwDecode.Core.Diagnostics;
using BmwDecode.Core.Translation;

namespace BmwDecode.Core.Tests;

/// <summary>Data pack round trips with synthetic data only (no BMW files in the repo).</summary>
public class DataPackTests
{
    private static string BuildPack(bool withIsta = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bmwdecode-test-{Guid.NewGuid():N}.pack");
        using var stream = File.Create(path);
        var w = new SgbdPackWriter(stream, "test");
        w.Add(new PackedSgbd
        {
            Name = "TESTDDE",
            Version = { ["REVISION"] = "1.0" },
            Jobs =
            {
                new PackedJob
                {
                    Name = "STATUS_RAILDRUCK_IST",
                    Comments = { "Raildruck lesen" },
                    Results = { new PackedParameter { Name = "STAT_RAILDRUCK_IST_WERT", Type = "real", Comments = { "Ergebnis" } } },
                },
                new PackedJob { Name = "_JOBS" },
            },
            Tables =
            {
                ["FORTTEXTE"] = [["ORT", "ORTTEXT"], ["0x4328", "4328 Botschaft fehlerhaft"], ["0x3E90", "3E90 Kurbelwellengeber"]],
            },
        });

        if (withIsta)
        {
            w.AddEntry("ista/labels.tsv", s =>
            {
                var b = Encoding.UTF8.GetBytes(
                    "C\t100\t17192\tP0600\tMessage (torque) incorrect\t\t\n" +
                    "C\t101\t17192\t\tInfo: torque request logged\t\t\n" +
                    "M\t200\t1\t\tFault currently present\t\t\n" +
                    "E\t300\t0\t\tBattery voltage\tV\tF_UBATT\n");
                s.Write(b);
            });
            w.AddJson("ista/ecu/TESTDDE.json", new IstaEcu
            {
                Name = "TESTDDE",
                Title = "Digital Diesel Electronics",
                Faults =
                {
                    new IstaFault { Code = "17192", DataType = "F", Label = "100", Modes = { "200" }, Environment = { "300" } },
                    new IstaFault { Code = "17192", DataType = "I", Label = "101" },
                },
                Functions =
                {
                    new IstaFunction
                    {
                        Path = "Status values", Title = "Rail pressure",
                        Jobs = { new IstaJob { Name = "STATUS_RAILDRUCK_IST", Results = { new IstaResult { Name = "STAT_RAILDRUCK_IST_WERT", Title = "Rail pressure, actual", Unit = "bar" } } } },
                    },
                },
            });
            w.AddEntry("ista/typekeys.tsv", s => s.Write(Encoding.UTF8.GetBytes(
                "TypeKey\tTypeKeyName\tBaseType\tProductType\tSeriesCode\tSeries\tGearbox\tPowerClass\tSteering\tEngine\tDisplacement\tFuel\tModel\tDrive\n" +
                "ZZ99\tZZ99\tZZ99\tP\tX'\tE84\tMECH\tO\tRL\tN47\t20\tD\tX1 sDrive20d\tRWD\n")));
            w.AddEntry("ista/vin/t.bin", s => VinRangeTable.Write(s,
            [
                new VinRange("T000001", "T000100", "ZZ98", "ZZ98", 2009, 5, "A"),
                new VinRange("T000050", "T000060", "ZZ99", "ZZ99", 2010, 3, "M"),
                new VinRange("T000200", "T000200", "ZZ99", "ZZ99", 2011, 1, "?"),
            ]));
            w.AddJson("ista/manifest.json", new IstaManifest { DatabaseVersion = "test", EcuCount = 1 });
        }

        w.Complete(t => t switch
        {
            "Raildruck lesen" => "Read rail pressure",
            "Ergebnis" => "Result",
            "4328 Botschaft fehlerhaft" => "4328 message faulty",
            _ => t,
        });
        return path;
    }

    [Fact]
    public void PackRoundTripsJobsTablesAndTranslations()
    {
        var path = BuildPack(withIsta: false);
        try
        {
            using var pack = SgbdPack.Open(path);
            Assert.Equal(1, pack.Count);
            Assert.Equal(2, pack.Manifest.JobCount);
            var s = pack.Get("testdde");
            Assert.NotNull(s);
            Assert.Equal("1.0", s!.Version["REVISION"]);
            Assert.Equal("STAT_RAILDRUCK_IST_WERT", s.Job("status_raildruck_ist")!.Results[0].Name);
            Assert.Equal(3, s.Table("forttexte").Count);
            Assert.Equal("Read rail pressure", pack.English("Raildruck lesen"));
            Assert.Equal("unknown text", pack.English("unknown text"));
            Assert.Null(pack.Ista);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PackedInspectorDescribesWithoutFiles()
    {
        var path = BuildPack(withIsta: false);
        try
        {
            using var pack = SgbdPack.Open(path);
            var inspector = new PackedSgbdInspector(pack, fallback: null);
            Assert.True(inspector.IsAvailable);
            var d = await inspector.DescribeAsync("TESTDDE");
            Assert.Single(d.Jobs); // virtual _JOBS hidden
            Assert.Equal("Raildruck lesen", d.Jobs[0].Comment);
            var j = await inspector.DescribeJobAsync("TESTDDE", "STATUS_RAILDRUCK_IST");
            Assert.Equal("real", j.Results[0].Type);
            var t = await inspector.ReadTableAsync("TESTDDE", "FORTTEXTE");
            Assert.Equal("0x4328", t[1][0]);
            Assert.Empty(await inspector.ReadTableAsync("NOPE", "FORTTEXTE"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TranslatorUsesExactPackTextsFirst()
    {
        var t = Translator.FromTables([new("botschaft", "message")], []);
        t.UseExact(new Dictionary<string, string> { ["4328 Botschaft fehlerhaft"] = "4328 message faulty (pack)" });
        Assert.Equal("4328 message faulty (pack)", t.Translate("4328 Botschaft fehlerhaft"));
        Assert.Equal(1, t.ExactCount);
        t.UseExact(null);
        Assert.NotEqual("4328 message faulty (pack)", t.Translate("4328 Botschaft fehlerhaft"));
    }

    [Fact]
    public void IstaSectionGivesFaultTextsTypeKeysAndVins()
    {
        var path = BuildPack();
        try
        {
            using var pack = SgbdPack.Open(path);
            var ista = pack.Ista!;
            Assert.Equal("Message (torque) incorrect", ista.FaultText("TESTDDE", 0x4328));
            Assert.Null(ista.FaultText("TESTDDE", 0x1234));
            Assert.Equal("Info: torque request logged", ista.FaultText("TESTDDE", 0x4328, infoMemory: true));
            var fault = ista.Ecu("testdde")!.FaultByCode(17192)!;
            Assert.Equal("Fault currently present", ista.Label(fault.Modes[0])!.Title);
            Assert.Equal("V", ista.Label(fault.Environment[0])!.Unit);
            Assert.Equal("bar", ista.Ecu("TESTDDE")!.Functions[0].Jobs[0].Results[0].Unit);

            Assert.Equal("E84", ista.TypeKey("zz99")!["Series"]);

            // Overlapping ranges: the VIN's own type (positions 4-7) picks the right one.
            var r = ista.FindVin("WBAZZ99000T000055")!;
            Assert.Equal("ZZ99", r.TypeKey);
            Assert.Equal((2010, 3, "M"), (r.Year, r.Month, r.GearBox));
            Assert.Equal("ZZ98", ista.FindVin("WBAZZ98000T000055")!.TypeKey);
            Assert.Equal("ZZ99", ista.FindVin("T000200")!.TypeKey);

            // Short VIN inside two ranges: first in file order, without VIN type and gearbox (EdiabasLib rule).
            var shortVin = ista.FindVin("T000055")!;
            Assert.Equal(("ZZ98", "", ""), (shortVin.TypeKey, shortVin.VinTypeKey, shortVin.GearBox));
            Assert.Null(ista.FindVin("WBAZZ99000T000150"));
            Assert.Null(ista.FindVin("WBAZZ99000X000001")); // no band file
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReferenceDataFeedsFaultParserAndVinLookup()
    {
        var path = BuildPack();
        var previous = ReferenceData.Pack;
        var translator = Translator.Current;
        try
        {
            Translator.Current = Translator.FromTables([], []);
            ReferenceData.Pack = SgbdPack.Open(path);

            var set = new ResultSet([
                new ResultValue("F_ORT_NR", ResultKind.Integer, 0x4328L),
                new ResultValue("F_ORT_TEXT", ResultKind.Text, "4328 Botschaft fehlerhaft"),
            ]);
            var faults = FaultParser.ParseFaults(new JobResult("TESTDDE", "TESTDDE", "FS_LESEN", "", new ResultSet([]), [set], TimeSpan.Zero));
            Assert.Equal("Message (torque) incorrect", faults[0].Text);
            Assert.Equal("4328 Botschaft fehlerhaft", faults[0].OriginalText);

            var vin = ReferenceData.FindVin("WBAZZ99000T000055")!;
            Assert.Equal(("E84", "Right-hand drive", "2.0 L"), (vin.Series, vin.Steering, vin.Displacement));
            Assert.Equal(new DateOnly(2010, 3, 1), vin.Production);
            Assert.Null(vin.SeenAt);
        }
        finally
        {
            ReferenceData.Pack?.Dispose();
            ReferenceData.Pack = previous;
            Translator.Current = translator;
            File.Delete(path);
        }
    }

    [Fact]
    public void EbcdicOrderPutsLettersBeforeDigits()
    {
        Assert.True(VinRangeTable.Ebcdic("A", "1") < 0);
        Assert.True(string.CompareOrdinal("A", "1") > 0);
        Assert.Equal(0, VinRangeTable.Ebcdic("VL53148", "VL53148"));
    }

    private static VinRangeTable.Band Band(IReadOnlyList<VinRange> fileOrder)
    {
        var ms = new MemoryStream();
        VinRangeTable.Write(ms, fileOrder);
        return VinRangeTable.Load(ms.ToArray(), (int)ms.Length)!;
    }

    [Fact]
    public void VinMatchingFollowsEdiabasLibRules()
    {
        var band = Band(
        [
            new VinRange("A000040", "A000070", "ZZ99", "ZZ99", 2010, 1, "M"),
            new VinRange("A000050", "A000060", "ZZ99", "ZZ97", 2010, 2, "A"),
            new VinRange("A000055", "A000055", "ZZ99", "ZZ96", 2010, 3, "A"),
        ]);

        // An exact single-VIN range wins.
        Assert.Equal("ZZ96", VinRangeTable.Find(band, "A000055", "ZZ99")!.TypeKey);
        // Two ranges of the same VIN type: ambiguous with a full VIN.
        Assert.Null(VinRangeTable.Find(band, "A000052", "ZZ99"));
        // Short VIN only: first in file order, gearbox unknown.
        var r = VinRangeTable.Find(band, "A000052", null)!;
        Assert.Equal(("ZZ99", ""), (r.TypeKey, r.GearBox));
        // Unsorted files: file order, not start order, decides.
        var unsorted = Band(
        [
            new VinRange("B000050", "B000060", "ZZ01", "ZZ01", 2011, 1, "M"),
            new VinRange("B000010", "B000090", "ZZ02", "ZZ02", 2011, 2, "M"),
        ]);
        Assert.Equal("ZZ01", VinRangeTable.Find(unsorted, "B000055", null)!.TypeKey);
    }

    [Fact]
    public void LongRangesStartingFarBackAreFound()
    {
        var ranges = new List<VinRange> { new("C000000", "C999999", "ZZ50", "ZZ50", 2012, 6, "A") };
        for (var i = 1; i < 300_000; i++)
        {
            var v = "C" + i.ToString("000000", System.Globalization.CultureInfo.InvariantCulture);
            ranges.Add(new VinRange(v, v, "ZZ51", "ZZ51", 2012, 7, "M"));
        }

        var band = Band(ranges);
        Assert.Equal("ZZ50", VinRangeTable.Find(band, "C999990", "ZZ50")!.TypeKey);
        Assert.Equal("ZZ51", VinRangeTable.Find(band, "C123456", "ZZ51")!.TypeKey);
        Assert.Null(VinRangeTable.Find(band, "D000001", null));
        Assert.Null(VinRangeTable.Load([1, 2, 3], 3));
    }

    [Fact]
    public void DisposedPackReturnsNothing()
    {
        var path = BuildPack();
        try
        {
            var pack = SgbdPack.Open(path);
            var ista = pack.Ista!;
            pack.Dispose();
            Assert.True(pack.IsDisposed);
            Assert.Null(pack.Get("TESTDDE"));
            Assert.Null(ista.FindVin("WBAZZ99000T000055"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InfoMemoryJobsAreRecognised()
    {
        Assert.True(FaultParser.IsInfoMemoryJob("IS_LESEN"));
        Assert.True(FaultParser.IsInfoMemoryJob("IS_LESEN_DETAIL"));
        Assert.False(FaultParser.IsInfoMemoryJob("FS_LESEN"));
    }
}
