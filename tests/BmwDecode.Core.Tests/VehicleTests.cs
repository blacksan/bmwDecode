using BmwDecode.Core.Catalog;
using BmwDecode.Core.Diagnostics;
using BmwDecode.Core.Vehicle;

namespace BmwDecode.Core.Tests;

public class VehicleTests
{
    [Fact]
    public void European_vin_is_decoded_without_a_model_year()
    {
        var d = VinDecoder.Decode("WBAVN32000VL53148");

        Assert.True(d.IsValid);
        Assert.Equal("BMW AG – passenger car", d.Manufacturer);
        Assert.Equal("VN32", d.TypeKey);
        Assert.Equal("VL53148", d.ShortVin);
        Assert.Equal("Leipzig, Germany", d.Plant);
        Assert.False(d.IsNorthAmericanFormat); // check digit happens to match, but position 10 is a filler
        Assert.Null(d.ModelYear);
    }

    [Fact]
    public void North_american_vin_checks_digit_and_year()
    {
        var d = VinDecoder.Decode("WBAPH57549VL77160");

        Assert.True(d.IsNorthAmericanFormat);
        Assert.True(d.CheckDigitValid);
        Assert.Equal(2009, d.ModelYear);
        Assert.Equal("Leipzig, Germany", d.Plant);

        var us = VinDecoder.Decode("4USCC7327SLA00196", productionYearHint: 1995);
        Assert.Equal(1995, us.ModelYear);
        Assert.Equal("Spartanburg, USA", us.Plant);
        Assert.Equal('7', VinDecoder.ComputeCheckDigit("4USCC7327SLA00196"));
    }

    [Fact]
    public void Short_vin_and_bad_input_are_handled()
    {
        var s = VinDecoder.Decode(" vl-53148 ");
        Assert.True(s.IsValid);
        Assert.Equal("VL53148", s.ShortVin);
        Assert.Equal("Leipzig, Germany", s.Plant);

        Assert.False(VinDecoder.Decode("WBAVN32000VL5314O").IsValid); // letter O
        Assert.False(VinDecoder.Decode("ABC").IsValid);
        Assert.False(VinDecoder.Decode("XXXVN32000VL53148").IsValid); // not a BMW manufacturer code
    }

    [Fact]
    public void Vehicle_order_yields_type_key_date_paint_and_options()
    {
        var o = VehicleOrderParser.Parse("E84_#0909*VN31%0475&LCSW$205$248$302-A8B+HO01")!;

        Assert.Equal("E84", o.Series);
        Assert.Equal("VN31", o.TypeKey);
        Assert.Equal(new DateOnly(2009, 9, 1), o.ProductionDate);
        Assert.Equal("0475", o.Paint);
        Assert.Equal("LCSW", o.Upholstery);
        Assert.Equal(new[] { "205", "248", "302" }, o.Options.ToArray());
        Assert.Contains("A8B", o.EWords);
        Assert.Contains("HO01", o.HoWords);
        Assert.Null(VehicleOrderParser.Parse("  "));
    }

    [Fact]
    public void Engine_is_inferred_from_the_engine_sgbd()
    {
        Assert.Equal("M57 diesel (DDE 6.0)", EngineHints.Describe("D60M57A0"));
        Assert.Equal("N47 diesel (DDE 7.3)", EngineHints.Describe("D73N47A0"));
        Assert.Equal("N52 straight-six", EngineHints.Describe("MSV70"));
        Assert.Equal("N53/N54 straight-six", EngineHints.Describe("MSD80"));
        Assert.Equal("M54 straight-six", EngineHints.Describe("MS430DS0"));
        Assert.Null(EngineHints.Describe(null));
    }

    [Fact]
    public void Every_E_series_chassis_has_a_profile_including_classics()
    {
        var all = VehicleCatalog.Default.ESeries.Select(s => s.Series).ToHashSet();
        foreach (var s in new[] { "E30", "E31", "E32", "E34", "E36", "E36/7", "E38", "E39", "E46", "E52", "E53", "E60", "E65", "E70", "E83", "E84", "E85", "E89", "E90", "E93" })
        {
            Assert.Contains(s, all);
        }

        Assert.DoesNotContain("E169", all); // motorbike entry is not a car profile
        var e34 = VehicleCatalog.Default.Find("E34")!;
        Assert.True(e34.IsDs2);
        Assert.True(e34.IsGeneric);
        Assert.Contains(e34.Ecus, e => e.PrimaryGroup.Equals("D_MOTOR", StringComparison.OrdinalIgnoreCase) || e.Address == 0x12);
        Assert.Equal("ZCS_E39", VehicleCatalog.Default.Find("E39")!.IdentSgbd);
        Assert.Null(VehicleCatalog.Default.Find("E84")!.IdentSgbd);
    }

    [Fact]
    public void Ds2_faults_use_the_present_bit_and_fault_type_texts()
    {
        static ResultValue T(string n, string v) => new(n, ResultKind.Text, v);
        static ResultValue I(string n, long v) => new(n, ResultKind.Integer, v);
        var set = new ResultSet([I("F_ORT_NR", 0x22), T("F_ORT_TEXT", "Lambdasonde"), I("F_VORHANDEN", 1),
            T("F_ART1_TEXT", "Fehler momentan vorhanden"), T("F_ART2_TEXT", "--"), T("F_ART3_TEXT", "Fehler abgasrelevant"),
            T("F_UW1_TEXT", "Motordrehzahl"), T("F_UW1_WERT", "780"), T("F_UW1_EINH", "1/min")]);
        var job = new JobResult("D_0012", "MS430DS0", "FS_LESEN", "", new ResultSet([]), [set, new ResultSet([T("JOB_STATUS", "OKAY")])], TimeSpan.Zero);

        var f = FaultParser.ParseFaults(job).Single();

        Assert.True(f.IsPresent);
        Assert.Equal("Fehler momentan vorhanden; Fehler abgasrelevant", f.Symptom);
        var detail = FaultParser.DetailFromEntry(f);
        Assert.Equal("Motordrehzahl", detail.Conditions.Single().Name);
        Assert.Equal("1/min", detail.Conditions.Single().Unit);
    }

    [Fact]
    public void Nhtsa_response_is_parsed()
    {
        var info = NhtsaVinClient.Parse("""
            {"Results":[{"Make":"BMW","Model":"328i","ModelYear":"2009","BodyClass":"Sedan/Saloon","DisplacementL":"3.0",
            "PlantCity":"Leipzig","ErrorCode":"0","ErrorText":"0 - VIN decoded clean.","Trim":""}]}
            """);

        Assert.Equal("328i", info["Model"]);
        Assert.Equal("3.0", info["Displacement (L)"]);
        Assert.Null(info["Trim"]);
        Assert.Null(info.Message);
    }
}

public class TypeKeyDatabaseTests
{
    [Fact]
    public void Fzgident_rows_and_user_csv_are_merged_with_csv_winning()
    {
        var gm = TypeKeyDatabase.FromFzgIdent(
        [
            ["GMTYP", "TYP", "BR", "VERK", "MOTOR", "ANTR", "GETR", "HUBR", "KAROS", "LEIST", "LENK", "TUER", "LAND", "REIHE"],
            ["8301", "0T16", "E93", "325I OL", "N53", "HECK", "MECH", "25.00", "CABRIO", "160", "LL", "2", "EUR", "3"],
        ]).Single();

        Assert.Equal("0T16", gm.TypeKey);
        Assert.Equal("2.5 L", gm.Displacement);
        Assert.Equal("160 kW", gm.Power);

        using var csv = new StringReader("# my notes\ntype_key,series,model,engine\n0T16,E93,325i Convertible,N53B30\nVN31,E84,\"X1 xDrive20d, test\",N47\n");
        var rows = TypeKeyDatabase.FromCsv(csv, "typekeys.csv").ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("X1 xDrive20d, test", rows[1].Model);
    }

    [Fact]
    public async Task Database_loads_from_inspector_tables()
    {
        var sim = new BmwDecode.Core.Simulation.SimulatedEngine(fast: true);
        var db = await TypeKeyDatabase.LoadAsync(sim, null);

        Assert.Equal("X1 DEMO M57 RWD", db.Find("dm84")?.Model);
        Assert.Null(db.Find("ZZZZ"));
        Assert.Single(db.Sources);
    }

    [Fact]
    public async Task Demo_car_identification_includes_order_and_engine()
    {
        var sim = new BmwDecode.Core.Simulation.SimulatedEngine(fast: true);
        await sim.ConnectAsync(new ConnectionSettings { Kind = InterfaceKind.Demo });
        var db = await TypeKeyDatabase.LoadAsync(sim, null);

        var id = await new BmwDecode.Core.Services.VehicleIdentifier(sim, db).IdentifyAsync(VehicleCatalog.Default.Find("E84"));

        Assert.Equal("E84", id.Series);
        Assert.Equal("DM84", id.Details.TypeKey);
        Assert.Equal("X1 DEMO M57 RWD", id.Details.Model);
        Assert.Equal("D60M57A0", id.Details.EngineEcu);
        Assert.Equal(new DateOnly(2009, 9, 1), id.Details.ProductionDate);
        Assert.Contains("205", id.Details.Options);
        Assert.Contains(id.Details.Sources, s => s.StartsWith("VIN from CAS"));
    }
}
