using System.Windows;

namespace BmwDecode.App.Services;

/// <summary>Applies the Windows 11 Fluent theme (light / dark / follow Windows) with Mica.</summary>
public sealed class ThemeService
{
    public void Apply(AppTheme theme)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

#pragma warning disable WPF0001 // Fluent ThemeMode is marked experimental in .NET 10
        app.ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
    }
}
