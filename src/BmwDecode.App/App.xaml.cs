using System.Windows;
using System.Windows.Threading;
using BmwDecode.App.Services;
using BmwDecode.App.ViewModels;
using BmwDecode.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace BmwDecode.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private System.Threading.Tasks.Task? _packLoad;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var services = new ServiceCollection();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<IUiThread, WpfUiThread>();
        services.AddSingleton<IClipboardService, WpfClipboard>();
        services.AddSingleton<IUiTimerFactory, WpfTimerFactory>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<Navigator>();
        services.AddSingleton<OverviewViewModel>();
        services.AddSingleton<VehicleViewModel>();
        services.AddSingleton<QuickTestViewModel>();
        services.AddSingleton<ModulesViewModel>();
        services.AddSingleton<JobExplorerViewModel>();
        services.AddSingleton<LogViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        _services = services.BuildServiceProvider();

        var settings = _services.GetRequiredService<SettingsStore>();
        settings.Load();

        // Offline German -> English translation of SGBD texts; translations.tsv lets the user correct any line.
        var translator = BmwDecode.Core.Translation.Translator.CreateDefault(
            System.IO.Path.Combine(SettingsStore.Folder, "translations.tsv"));
        translator.Enabled = settings.Current.TranslateTexts;
        BmwDecode.Core.Translation.Translator.Current = translator;

        // Built-in reference data (sgbd.pack next to the app): job/table descriptions, precomputed English texts,
        // BMW's English fault texts, type keys and VIN ranges. Loaded off the UI thread; everything works without it.
        _packLoad = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                BmwDecode.Core.Data.ReferenceData.Pack = BmwDecode.Core.Data.SgbdPack.TryOpenDefault(
                    System.IO.Path.Combine(SettingsStore.Folder, "Data"));
            }
            catch (Exception ex)
            {
                _services?.GetService<SessionService>()?.Error($"Data pack could not be loaded: {ex.Message}");
            }
        });
        _services.GetRequiredService<ThemeService>().Apply(settings.Current.Theme);

        var window = new MainWindow { DataContext = _services.GetRequiredService<MainViewModel>() };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // Close the interface so the COM port / FTDI device is released. The engine never needs the UI
            // thread (ConfigureAwait(false) throughout), so waiting here cannot deadlock.
            _services?.GetService<SessionService>()?.Engine?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception)
        {
            // Shutting down: nothing useful to do with a failed disconnect.
        }

        _services?.Dispose();
        // Let a still-running load finish, unpublish the pack, then close it; lookups after that return nothing.
        try
        {
            _packLoad?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        var pack = BmwDecode.Core.Data.ReferenceData.Pack;
        BmwDecode.Core.Data.ReferenceData.Pack = null;
        pack?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.GetService<SessionService>()?.Error($"Unexpected error: {e.Exception}");
        MessageBox.Show(e.Exception.Message, "bmwDecode – unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
