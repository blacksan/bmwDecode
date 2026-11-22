using BmwDecode.App.Services;
using BmwDecode.Core.Diagnostics;
using BmwDecode.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BmwDecode.App.ViewModels;

/// <summary>Shell: navigation pane, current page and status bar.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(Navigator navigator, SessionService session,
        OverviewViewModel overview, VehicleViewModel vehicle, QuickTestViewModel quickTest, ModulesViewModel modules,
        JobExplorerViewModel jobs, LogViewModel log, SettingsViewModel settings, IUiTimerFactory timers, DataService data)
    {
        Data = data;
        Navigator = navigator;
        Session = session;
        Pages = [overview, vehicle, quickTest, modules, jobs, log];
        FooterPages = [settings];
        foreach (var page in Pages.Concat(FooterPages))
        {
            navigator.Register(page);
        }

        navigator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Navigator.Current))
            {
                SyncSelection();
            }
        };
        navigator.Current = overview;
        session.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(VehicleText));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsDemo));
            OnPropertyChanged(nameof(BatteryText));
            OnPropertyChanged(nameof(IgnitionText));
            OnPropertyChanged(nameof(IgnitionOn));
        };

        // Battery and ignition under Settings: refreshed while connected and no scan/live poll is using the bus.
        _supplyTimer = timers.Create(TimeSpan.FromSeconds(5));
        _supplyTimer.Tick += async (_, _) => await RefreshSupplyAsync();
        _supplyTimer.Start();
    }

    private readonly IUiTimer _supplyTimer;
    private bool _refreshingSupply;

    public string BatteryText => Session.IsConnected ? Session.Supply?.BatteryText ?? "…" : "—";

    public string IgnitionText => Session.IsConnected ? Session.Supply?.IgnitionText ?? "…" : "—";

    public bool? IgnitionOn => Session.IsConnected ? Session.Supply?.IgnitionOn : null;

    private async Task RefreshSupplyAsync()
    {
        if (_refreshingSupply || !Session.IsConnected || Session.IsBusy || Session.Engine is not { } engine)
        {
            return;
        }

        _refreshingSupply = true;
        try
        {
            Session.Supply = await new VehicleIdentifier(engine).ReadSupplyAsync();
        }
        catch (DiagnosticException)
        {
            // Interface busy or gone: try again on the next tick.
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _refreshingSupply = false;
        }
    }

    public Navigator Navigator { get; }

    /// <summary>BMW data import (shown in the navigation pane while it runs).</summary>
    public DataService Data { get; }
    public SessionService Session { get; }
    public IReadOnlyList<PageViewModel> Pages { get; }
    public IReadOnlyList<PageViewModel> FooterPages { get; }

    public bool IsConnected => Session.IsConnected;
    public bool IsDemo => Session.IsDemo;
    public string ConnectionText => Session.IsConnected ? Session.EngineName : "Not connected";

    public string VehicleText
    {
        get
        {
            var v = Session.Vehicle;
            var parts = new List<string>();
            if (Session.Series is { } s)
            {
                parts.Add($"Profile {s.Series}");
            }

            if (v.Vin is { } vin)
            {
                parts.Add($"VIN {vin}");
            }

            if (Session.Supply is { BatteryMillivolts: not null } supply)
            {
                parts.Add($"Battery {supply.BatteryText}");
            }

            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Selected item in the top navigation list (null while a footer page is shown).</summary>
    public PageViewModel? SelectedPage
    {
        get => Pages.Contains(Navigator.Current!) ? Navigator.Current : null;
        set
        {
            if (value is not null)
            {
                Navigator.Current = value;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedFooterPage));
        }
    }

    public PageViewModel? SelectedFooterPage
    {
        get => FooterPages.Contains(Navigator.Current!) ? Navigator.Current : null;
        set
        {
            if (value is not null)
            {
                Navigator.Current = value;
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedPage));
        }
    }

    /// <summary>Keeps the nav selection in sync when pages navigate programmatically.</summary>
    public void SyncSelection()
    {
        OnPropertyChanged(nameof(SelectedPage));
        OnPropertyChanged(nameof(SelectedFooterPage));
    }
}
