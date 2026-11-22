using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using BmwDecode.Core.Services;

namespace BmwDecode.Mac;

/// <summary>Quick test state (or a fault's present flag) to a status brush.</summary>
public sealed class ScanStateToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Ignition / connection: on = green, off = amber / grey.
        if (parameter is "ignition" or "connected")
        {
            return Lookup(value switch
            {
                true => "SuccessBrush",
                false when parameter is "ignition" => "CautionBrush",
                _ => parameter is "ignition" ? "SecondaryTextBrush" : "NeutralBrush",
            });
        }

        var key = value switch
        {
            EcuScanState.NoFaults => "SuccessBrush",
            EcuScanState.Faults => "CriticalBrush",
            EcuScanState.Error => "CautionBrush",
            EcuScanState.Scanning => "AccentTextBrush",
            true => "CriticalBrush",
            _ => "NeutralBrush",
        };
        return Lookup(key);
    }

    private static IBrush Lookup(string key)
    {
        var app = Application.Current;
        return app is not null && app.TryGetResource(key, app.ActualThemeVariant, out var brush) && brush is IBrush b
            ? b
            : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when a count is zero (empty-state hints), or non-zero with Invert.</summary>
public sealed class ZeroToTrueConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => (value is 0) != Invert;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Navigation icons: the shared view models carry Segoe Fluent glyphs, which macOS does not have.</summary>
public sealed class PageIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        "Overview" => "⌂",
        "Vehicle / VIN" => "🚗",
        "Quick test" => "⚡",
        "Modules" => "▦",
        "Log" => "☰",
        "Settings" => "⚙",
        _ => "•",
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
