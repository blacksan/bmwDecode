using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using BmwDecode.App.Services;

namespace BmwDecode.Mac.Services;

internal static class Shell
{
    public static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}

public sealed class AvaloniaUiThread : IUiThread
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public void Invoke(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Invoke(action);
        }
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

public sealed class AvaloniaTimerFactory : IUiTimerFactory
{
    public IUiTimer Create(TimeSpan interval) => new Timer(interval);

    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public Timer(TimeSpan interval)
        {
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += (s, e) => Tick?.Invoke(this, e);
        }

        public event EventHandler? Tick;

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();
    }
}

public sealed class AvaloniaClipboard : IClipboardService
{
    public async Task<bool> TrySetTextAsync(string text)
    {
        if (Shell.MainWindow?.Clipboard is not { } clipboard)
        {
            return false;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed class AvaloniaThemeService : IThemeService
{
    public void Apply(AppTheme theme)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                AppTheme.Light => ThemeVariant.Light,
                AppTheme.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }
}

/// <summary>Mac sheets for confirmations and the native open/save panels for files.</summary>
public sealed class AvaloniaDialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, bool warning = true) =>
        ShowAsync(title, message, warning ? "⚠" : "?", "Continue", "Cancel");

    public Task ShowErrorAsync(string title, string message) => ShowAsync(title, message, "⛔", "OK", null);

    public async Task<string?> OpenFileAsync(string title, string filter)
    {
        if (Shell.MainWindow?.StorageProvider is not { } storage)
        {
            return null;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = FileFilter.Parse(filter).Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns }).ToList(),
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolderAsync(string title, string? initial)
    {
        if (Shell.MainWindow?.StorageProvider is not { } storage)
        {
            return null;
        }

        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
        {
            options.SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(initial);
        }

        var result = await storage.OpenFolderPickerAsync(options);
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }

    public async Task<string?> SaveFileAsync(string title, string fileName, string filter)
    {
        if (Shell.MainWindow?.StorageProvider is not { } storage)
        {
            return null;
        }

        var types = FileFilter.Parse(filter)
            .Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns })
            .ToList();
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = fileName,
            FileTypeChoices = types,
            DefaultExtension = Path.GetExtension(fileName).TrimStart('.'),
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private static async Task<bool> ShowAsync(string title, string message, string icon, string ok, string? cancel)
    {
        var owner = Shell.MainWindow;
        var result = false;
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        };

        var okButton = new Button { Content = ok, IsDefault = true, MinWidth = 90, Classes = { "accent" } };
        okButton.Click += (_, _) => { result = true; dialog.Close(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        if (cancel is not null)
        {
            var cancelButton = new Button { Content = cancel, IsCancel = true, MinWidth = 90 };
            cancelButton.Click += (_, _) => dialog.Close();
            buttons.Children.Add(cancelButton);
        }

        buttons.Children.Add(okButton);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = icon, FontSize = 28, VerticalAlignment = VerticalAlignment.Top },
                        new StackPanel
                        {
                            Spacing = 6,
                            Width = 370,
                            Children =
                            {
                                new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 15, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                            },
                        },
                    },
                },
                buttons,
            },
        };

        if (owner is not null)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }

        return result;
    }
}
