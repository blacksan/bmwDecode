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
            Translation.JobNames.Reset();
        }
    }

    public static IstaData? Ista => s_pack is { IsDisposed: false } p ? p.Ista : null;

    /// <summary>SourceFile of a vehicle record that came from the factory VIN ranges, not from ISTA history.</summary>
    public const string FactoryRangesSource = "factory VIN ranges";

    /// <summary>A damaged or closed pack must never break diagnostics: lookups just return nothing.</summary>
    private static bool IsDataError(Exception ex) =>
        ex is IOException or InvalidDataException or System.Text.Json.JsonException or ObjectDisposedException
            or ArgumentException or IndexOutOfRangeException or NotSupportedException or FormatException;

    /// <summary>BMW's English text for a fault (or, with <paramref name="infoMemory"/>, info memory) code of an ECU variant.</summary>
    public static string? FaultText(string? variant, long code, bool infoMemory = false)
    {
        if (Ista is not { } ista || string.IsNullOrWhiteSpace(variant))
        {
            return null;
        }

        try
        {
            return ista.FaultText(variant, code, infoMemory);
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            return null;
        }
    }

    /// <summary>Every type key in the pack, in bmwDecode's shape.</summary>
    public static IReadOnlyList<TypeKeyInfo> TypeKeys()
    {
        try
        {
            return Ista is { } ista
                ? ista.TypeKeys.Where(kv => kv.Key.Length == 4).Select(kv => ToTypeKeyInfo(kv.Key, kv.Value)).ToList()
                : [];
        }
        catch (Exception ex) when (IsDataError(ex))
        {
            return [];
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
        catch (Exception ex) when (IsDataError(ex))
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
            null, null, null, FactoryRangesSource);
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
