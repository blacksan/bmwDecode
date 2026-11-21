using System.Collections.ObjectModel;
using BmwDecode.App.Services;
using BmwDecode.Core.Catalog;
using BmwDecode.Core.Diagnostics;
using BmwDecode.Core.Services;
using BmwDecode.Ediabas;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BmwDecode.App.ViewModels;

public sealed record InterfaceOption(InterfaceKind Kind, string Label, string Hint);

/// <summary>Connect to the car, identify it and pick the chassis profile.</summary>
public sealed partial class OverviewViewModel : PageViewModel
{
    private readonly SessionService _session;
    private readonly SettingsStore _settings;
    private readonly Navigator _navigator;

    public OverviewViewModel(SessionService session, SettingsStore settings, Navigator navigator)
        : base("Overview", "\uE80F")
    {
        _session = session;
        _settings = settings;
        _navigator = navigator;

        Interfaces =
        [
            new(InterfaceKind.KDcanSerial, "K+DCAN USB cable (COM port)", "INPA-style cable. Set the COM port; FTDI latency timer 1 ms. E84/E9x: switch the cable to D-CAN (pins 7-8 open)."),
            new(InterfaceKind.Enet, "ENET cable (Ethernet)", "For F/G/I series and late E-series with an Ethernet port. 'auto' discovers the car."),
            new(InterfaceKind.Demo, "Demo vehicle (no car needed)", "Simulated E84 X1 with an M57 diesel, E90 CAS and RWD conversion."),
        ];

        _selectedInterface = Interfaces.FirstOrDefault(i => i.Kind == settings.Current.Interface) ?? Interfaces[0];
        _port = settings.Current.Port ?? "COM1";
        SeriesOptions = [.. session.Catalog.ESeries];
        RefreshPorts();

        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionService.IsBusy))
            {
                DisconnectCommand.NotifyCanExecuteChanged();
            }

            if (e.PropertyName is nameof(SessionService.Engine) or nameof(SessionService.Vehicle) or nameof(SessionService.Supply) or nameof(SessionService.Series))
            {
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(Vehicle));
                OnPropertyChanged(nameof(Supply));
                OnPropertyChanged(nameof(ConnectionTitle));
                OnPropertyChanged(nameof(SelectedSeries));
                OnPropertyChanged(nameof(SeriesMismatch));
                ConnectCommand.NotifyCanExecuteChanged();
                DisconnectCommand.NotifyCanExecuteChanged();
                IdentifyCommand.NotifyCanExecuteChanged();
                QuickTestCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public IReadOnlyList<InterfaceOption> Interfaces { get; }

    public ObservableCollection<string> Ports { get; } = [];

    public IReadOnlyList<SeriesDefinition> SeriesOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsPort), nameof(PortLabel))]
    private InterfaceOption _selectedInterface;

    [ObservableProperty]
    private string _port;

    [ObservableProperty]
    private string? _errorText;

    public bool IsConnected => _session.IsConnected;
    public VehicleIdentity Vehicle => _session.Vehicle;
    public SupplyStatus? Supply => _session.Supply;
    public bool NeedsPort => SelectedInterface.Kind != InterfaceKind.Demo;

    public string PortLabel => SelectedInterface.Kind switch
    {
        InterfaceKind.Enet => "Vehicle IP (or auto)",
        _ => "COM port",
    };

    public string ConnectionTitle => _session.IsConnected ? $"Connected · {_session.EngineName}" : "Not connected";

    public string EcuPathText => EcuPathLocator.CountSgbd(_settings.Current.EcuPath) is var n and > 0
        ? $"{_settings.Current.EcuPath} ({n:N0} SGBD files)"
        : "EDIABAS ECU folder not set – open Settings and point it at C:\\EDIABAS\\Ecu (from your INPA install).";

    public bool EcuPathValid => EcuPathLocator.IsValid(_settings.Current.EcuPath);

    public SeriesDefinition? SelectedSeries
    {
        get => _session.Series;
        set
        {
            if (value is not null && !ReferenceEquals(value, _session.Series))
            {
                _session.Series = value;
                _settings.Current.DefaultSeries = value.Series;
                _settings.Save();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SeriesMismatch));
            }
        }
    }

    /// <summary>Shown when the car reports a different chassis than the selected profile (e.g. a CAS from another car).</summary>
    public string? SeriesMismatch =>
        Vehicle.Series is { } detected && _session.Series is { } chosen && !detected.Equals(chosen.Series, StringComparison.OrdinalIgnoreCase)
            ? $"The vehicle order reports {detected}, but the {chosen.Series} profile is selected. Swapped modules (e.g. a CAS from another chassis) can cause this; keep the profile that matches the body."
            : null;

    public override void OnNavigatedTo()
    {
        OnPropertyChanged(nameof(EcuPathText));
        OnPropertyChanged(nameof(EcuPathValid));
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        Ports.Clear();
        if (SelectedInterface.Kind == InterfaceKind.Enet)
        {
            Ports.Add("auto");
            Ports.Add("169.254.1.1");
        }
        else
        {
            foreach (var p in EdiabasEngine.AvailablePorts())
            {
                Ports.Add(p);
            }

            if (Ports.Count == 0)
            {
                Ports.Add("COM1");
            }
        }

        if (string.IsNullOrWhiteSpace(Port) || (SelectedInterface.Kind != InterfaceKind.KDcanSerial && !Ports.Contains(Port)))
        {
            Port = Ports[0];
        }
    }

    partial void OnSelectedInterfaceChanged(InterfaceOption value)
    {
        Port = value.Kind switch
        {
            InterfaceKind.Enet => "auto",
            _ => (Port ?? string.Empty).StartsWith("COM", StringComparison.OrdinalIgnoreCase) ? Port! : "COM1",
        };
        RefreshPorts();
    }

    private bool CanConnect() => !IsConnected && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanConnect), IncludeCancelCommand = true)]
    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ErrorText = null;
        if (SelectedInterface.Kind != InterfaceKind.Demo && !EcuPathValid)
        {
            ErrorText = "Set the EDIABAS ECU folder in Settings first – bmwDecode reads the BMW SGBD files from your INPA/EDIABAS install.";
            return;
        }

        var settings = new ConnectionSettings
        {
            Kind = SelectedInterface.Kind,
            Port = Port?.Trim() ?? string.Empty,
            EcuPath = _settings.Current.EcuPath,
            Trace = _settings.Current.EdiabasTrace,
        };

        _settings.Current.Interface = settings.Kind;
        _settings.Current.Port = settings.Port;
        _settings.Save();

        await RunAsync("Connecting…", async () =>
        {
            await _session.ConnectAsync(settings, cancellationToken);
            _session.Info($"Connected: {_session.EngineName}");
            await IdentifyCoreAsync(cancellationToken);
        });
    }

    private bool CanDisconnect() => IsConnected && !IsWorking && !_session.IsBusy;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        await RunAsync("Disconnecting…", () => _session.DisconnectAsync());
        StatusText = "Disconnected.";
    }

    private bool CanIdentify() => IsConnected && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanIdentify))]
    private Task IdentifyAsync(CancellationToken cancellationToken) =>
        RunAsync("Identifying vehicle…", () => IdentifyCoreAsync(cancellationToken));

    [RelayCommand(CanExecute = nameof(CanIdentify))]
    private void QuickTest()
    {
        var page = _navigator.NavigateTo<QuickTestViewModel>();
        if (page.RunCommand.CanExecute(null))
        {
            page.RunCommand.Execute(null);
        }
    }

    private async Task IdentifyCoreAsync(CancellationToken cancellationToken)
    {
        var engine = _session.RequireEngine();
        var identifier = new VehicleIdentifier(engine);
        var vehicle = await identifier.IdentifyAsync(cancellationToken);
        _session.Supply = new SupplyStatus(vehicle.BatteryMillivolts, vehicle.IgnitionOn);
        _session.Vehicle = vehicle;

        // Switch profile only when the car belongs to a different family (e.g. E60 vs E89X). Within a family keep the
        // user's choice: on swapped cars (an E84 with an E90 CAS) the CAS reports the donor's chassis.
        if (vehicle.Series is { } detected && _session.Catalog.Find(detected) is { } series &&
            !string.Equals(series.Family, _session.Series?.Family, StringComparison.OrdinalIgnoreCase))
        {
            _session.Series = series;
        }

        StatusText = vehicle.Vin is null
            ? "Connected, but the VIN could not be read. Is the ignition on?"
            : $"VIN {vehicle.Vin} · {vehicle.Series ?? "series not reported"}";
        _session.Info(StatusText);
    }

    private async Task RunAsync(string busy, Func<Task> action)
    {
        IsWorking = true;
        StatusText = busy;
        ErrorText = null;
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        IdentifyCommand.NotifyCanExecuteChanged();
        try
        {
            await action();
            if (StatusText == busy)
            {
                StatusText = null;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (DiagnosticException ex)
        {
            ErrorText = ex.Message;
            StatusText = null;
            _session.Error(ex.Message);
        }
        finally
        {
            IsWorking = false;
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
            IdentifyCommand.NotifyCanExecuteChanged();
            QuickTestCommand.NotifyCanExecuteChanged();
        }
    }
}
