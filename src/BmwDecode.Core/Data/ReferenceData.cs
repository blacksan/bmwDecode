using System.Globalization;
using BmwDecode.Core.Vehicle;

namespace BmwDecode.Core.Data;

/// <summary>
/// The data pack the app is running with, shared by the parsers and identification. Set once at start-up
/// (App) or by tests; everything degrades to the SGBD texts and the translator when no pack is loaded.
/// </summary>
public static class ReferenceData
{
    private static SgbdPack? s_pack;

    public static SgbdPack? Pack
    {
        get => s_pack;
        set
        {
            s_pack = value;
            Translation.Translator.Current.UseExact(value?.Translations);
        }
    }

    public static IstaData? Ista => s_pack?.Ista;

    /// <summary>BMW's English fault text for a fault code of an ECU variant, or null.</summary>
    public static string? FaultText(string? variant, long code)
    {
        if (Ista is not { } ista || string.IsNullOrWhiteSpace(variant))
        {
            return null;
        }

        try
        {
            return ista.FaultText(variant, code);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Every type key in the pack, in bmwDecode's shape.</summary>
    public static IEnumerable<TypeKeyInfo> TypeKeys()
    {
        if (Ista is not { } ista)
        {
            yield break;
        }

        foreach (var (key, row) in ista.TypeKeys)
        {
            if (key.Length == 4)
            {
                yield return ToTypeKeyInfo(key, row);
            }
        }
    }

    /// <summary>
    /// Factory VIN range lookup: type key, production month and gearbox for any VIN BMW built, combined with the
    /// type key's attributes. Null when the pack has no VIN ranges or the VIN is not covered.
    /// </summary>
    public static IstaVehicleRecord? FindVin(string? vin)
    {
        if (Ista is not { } ista || vin is null || vin.Trim().Length is not (7 or 17))
        {
            return null;
        }

        VinRange? range;
        try
        {
            range = ista.FindVin(vin);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return null;
        }

        if (range is null)
        {
            return null;
        }

        var info = ista.TypeKey(range.TypeKey) is { } row ? ToTypeKeyInfo(range.TypeKey, row) : null;
        var gearbox = range.GearBox switch { "A" => "Automatic", "M" => "Manual", _ => info?.Gearbox };
        return new IstaVehicleRecord(
            vin.Trim().ToUpperInvariant(), range.TypeKey, info?.Series, info?.Model, info?.Engine, info?.Engine,
            info?.Displacement, gearbox, info?.Drive, info?.Body, info?.Market, info?.Steering,
            range.Year > 1980 && range.Month is >= 1 and <= 12 ? new DateOnly(range.Year, range.Month, 1) : null,
            null, null, null, "factory VIN ranges");
    }

    internal static TypeKeyInfo ToTypeKeyInfo(string key, IReadOnlyDictionary<string, string> row)
    {
        string? Get(string name) => row.TryGetValue(name, out var v) && v.Length > 0 && v != "-" ? v : null;

        var displacement = Get("Displacement");
        if (displacement is not null && int.TryParse(displacement, NumberStyles.None, CultureInfo.InvariantCulture, out var dl) && dl > 0)
        {
            displacement = $"{dl / 10.0:0.0} L"; // ISTA stores decilitres: 20 = 2.0 L
        }

        return new TypeKeyInfo(
            key.ToUpperInvariant(),
            Get("Series"),
            Get("Model"),
            Get("Engine"),
            Get("Drive"),
            Get("Gearbox") switch { "MECH" => "Manual", "AUT" => "Automatic", var g => g },
            displacement,
            Get("Body"),
            null,
            Get("Steering") switch { "LL" => "Left-hand drive", "RL" => "Right-hand drive", var s => s },
            Get("Doors"),
            Get("Market"),
            null,
            "ISTA type keys");
    }
}
