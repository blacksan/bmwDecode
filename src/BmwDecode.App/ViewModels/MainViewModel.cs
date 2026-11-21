using BmwDecode.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BmwDecode.App.ViewModels;

/// <summary>Shell: navigation pane, current page and status bar.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(Navigator navigator, SessionService session,
        OverviewViewModel overview, VehicleViewModel vehicle, QuickTestViewModel quickTest, ModulesViewModel modules,
        JobExplorerViewModel jobs, LogViewModel log, SettingsViewModel settings)
    {
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
        };
    }

    public Navigator Navigator { get; }
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
