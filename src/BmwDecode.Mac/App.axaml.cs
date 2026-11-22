using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BmwDecode.App.Services;
using BmwDecode.App.ViewModels;
using BmwDecode.Core.Data;
using BmwDecode.Core.Translation;
using BmwDecode.Mac.Services;
using Microsoft.Extensions.DependencyInjection;

namespace BmwDecode.Mac;

public partial class App : Application
{
    private ServiceProvider? _services;
    private Task? _packLoad;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<IThemeService, AvaloniaThemeService>();
        services.AddSingleton<IDialogService, AvaloniaDialogService>();
        services.AddSingleton<IUiThread, AvaloniaUiThread>();
        services.AddSingleton<IClipboardService, AvaloniaClipboard>();
        services.AddSingleton<IUiTimerFactory, AvaloniaTimerFactory>();
        services.AddSingleton<DataService>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<Navigator>();
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<VehicleViewModel>();
        services.AddSingleton<QuickTestViewModel>();
        services.AddSingleton<ModulesViewModel>();
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        _services = services.BuildServiceProvider();

        var settings = _services.GetRequiredService<SettingsStore>();
        settings.Load();

        // Offline German -> English translation of SGBD texts; translations.tsv lets the user correct any line.
        var translator = Translator.CreateDefault(Path.Combine(SettingsStore.Folder, "translations.tsv"));
        translator.Enabled = settings.Current.TranslateTexts;
        Translator.Current = translator;

        // Built-in reference data: inside the .app (Contents/MacOS/Data) or ~/Library/Application Support/bmwDecode/Data.
        _packLoad = Task.Run(() =>
        {
            try
            {
                ReferenceData.Pack = SgbdPack.TryOpenDefault(Path.Combine(SettingsStore.Folder, "Data"));
            }
            catch (Exception ex)
            {
                _services?.GetService<SessionService>()?.Error($"Data pack could not be loaded: {ex.Message}");
            }
        });

        _services.GetRequiredService<IThemeService>().Apply(settings.Current.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow { DataContext = _services.GetRequiredService<MainViewModel>() };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => Shutdown();

            // First start on a clean Mac: download the ECU files and data pack from the private data server.
            var data = _services.GetRequiredService<DataService>();
            var packLoad = _packLoad;
            window.Opened += async (_, _) => await data.StartupAsync(packLoad);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Shutdown()
    {
        try
        {
            // Release the serial port; the engine never needs the UI thread, so waiting cannot deadlock.
            _services?.GetService<SessionService>()?.Engine?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
        }

        _services?.Dispose();
        try
        {
            _packLoad?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        var pack = ReferenceData.Pack;
        ReferenceData.Pack = null;
        pack?.Dispose();
    }
}
