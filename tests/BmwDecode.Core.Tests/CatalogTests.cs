using BmwDecode.Core.Catalog;

namespace BmwDecode.Core.Tests;

public class CatalogTests
{
    [Fact]
    public void Embedded_catalogue_contains_E84_in_the_E89X_family()
    {
        var e84 = VehicleCatalog.Default.Find("E84");

        Assert.NotNull(e84);
        Assert.Equal("E89X", e84!.Family);
        Assert.Equal("BN2000", e84.BusType);
    }

    [Fact]
    public void E84_has_engine_gearbox_dsc_cas_and_gateway_at_standard_addresses()
    {
        var e84 = VehicleCatalog.Default.Find("e84")!;

        Assert.Equal("D_MOTOR", e84.Ecus.Single(e => e.Address == 0x12).PrimaryGroup);
        Assert.Equal("D_EGS", e84.Ecus.Single(e => e.Address == 0x18).PrimaryGroup);
        Assert.Equal("D_DSC", e84.Ecus.Single(e => e.Address == 0x29).PrimaryGroup);
        Assert.Equal("D_CAS", e84.Ecus.Single(e => e.Address == 0x40).PrimaryGroup);
        Assert.Equal("D_ZGM", e84.Ecus.Single(e => e.Address == 0x00).PrimaryGroup);
    }

    [Fact]
    public void Virtual_and_placeholder_entries_are_dropped()
    {
        var e84 = VehicleCatalog.Default.Find("E84")!;

        Assert.DoesNotContain(e84.Ecus, e => e.Abbreviation.StartsWith("VIRTSG", StringComparison.Ordinal));
        Assert.DoesNotContain(e84.Ecus, e => e.Groups.Any(g => g.EndsWith('_')));
    }

    [Fact]
    public void All_common_E_series_chassis_are_present()
    {
        var names = VehicleCatalog.Default.ESeries.Select(s => s.Series).ToHashSet();

        foreach (var s in new[] { "E46", "E60", "E70", "E81", "E82", "E84", "E87", "E88", "E90", "E91", "E92", "E93" })
        {
            Assert.Contains(s, names);
        }
    }

    [Fact]
    public void Ecu_names_are_human_readable()
    {
        Assert.Equal("Car access system (keys, immobiliser, start)", EcuNames.Describe("CAS"));
        Assert.Equal("Telephone / Bluetooth (MULF)", EcuNames.Describe("TEL/MULF"));
        Assert.Equal("Seat heating, driver", EcuNames.Describe("FAH/XYZ"));
        Assert.Equal("UNKNOWNX", EcuNames.Describe("UNKNOWNX"));
        Assert.Equal("X1 (E84)", SeriesNames.Describe("E84"));
    }
}
