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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        var services = new ServiceCollection();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<DialogService>();
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
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _services?.GetService<SessionService>()?.Error($"Unexpected error: {e.Exception}");
        MessageBox.Show(e.Exception.Message, "bmwDecode – unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
