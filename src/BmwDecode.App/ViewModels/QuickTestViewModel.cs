using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using BmwDecode.App.Services;
using BmwDecode.Core.Catalog;
using BmwDecode.Core.Diagnostics;
using BmwDecode.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BmwDecode.App.ViewModels;

/// <summary>One row of the quick test table.</summary>
public sealed partial class EcuRowViewModel : ObservableObject
{
    public EcuRowViewModel(EcuDefinition ecu) => Ecu = ecu;

    public EcuDefinition Ecu { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsFitted), nameof(FaultCount), nameof(PartNumber), nameof(Variant))]
    private EcuScanResult? _result;

    public EcuScanState State => Result?.State ?? EcuScanState.Pending;
    public string? Variant => Result?.Variant;
    public string? PartNumber => Result?.PartNumber;
    public int FaultCount => Result?.FaultList.Count ?? 0;
    public bool IsFitted => Result?.IsFitted == true;

    public string StatusText => State switch
    {
        EcuScanState.Pending => "Waiting",
        EcuScanState.Scanning => "Reading…",
        EcuScanState.NoFaults => "No faults",
        EcuScanState.Faults => FaultCount == 1 ? "1 fault" : $"{FaultCount} faults",
        EcuScanState.NotFitted => "Not fitted / no answer",
        _ => Result?.Error ?? "Error",
    };

    partial void OnResultChanged(EcuScanResult? value) => OnPropertyChanged(nameof(State));
}

/// <summary>INPA-style quick test across every module of the chassis.</summary>
public sealed partial class QuickTestViewModel : PageViewModel
{
    private readonly SessionService _session;
    private readonly SettingsStore _settings;
    private readonly DialogService _dialogs;
    private readonly Navigator _navigator;

    public QuickTestViewModel(SessionService session, SettingsStore settings, DialogService dialogs, Navigator navigator)
        : base("Quick test", "")
    {
        _session = session;
        _settings = settings;
        _dialogs = dialogs;
        _navigator = navigator;
        _readIdent = settings.Current.ReadIdentInQuickTest;

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => ShowAll || o is EcuRowViewModel { State: not EcuScanState.NotFitted };

        _session.ConnectionChanged += (_, _) =>
        {
            if (RunCancelCommand.CanExecute(null))
            {
                RunCancelCommand.Execute(null);
            }

            RefreshCommands();
        };
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionService.Series))
            {
                OnPropertyChanged(nameof(SeriesName));
                RefreshCommands();
                if (!IsWorking)
                {
                    Rows.Clear();
                }
            }
        };
    }

    public ObservableCollection<EcuRowViewModel> Rows { get; } = [];

    public ICollectionView RowsView { get; }

    public string SeriesName => _session.Series?.DisplayName ?? "No chassis selected";

    [ObservableProperty]
    private bool _readIdent;

    [ObservableProperty]
    private bool _showAll;

    [ObservableProperty]
    private int _done;

    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    private int _found;

    [ObservableProperty]
    private int _faultTotal;

    [ObservableProperty]
    private string? _current;

    [ObservableProperty]
    private EcuRowViewModel? _selectedRow;

    partial void OnShowAllChanged(bool value) => RowsView.Refresh();

    partial void OnReadIdentChanged(bool value)
    {
        _settings.Current.ReadIdentInQuickTest = value;
        _settings.Save();
    }

    private bool CanRun() => _session.IsConnected && _session.Series is not null && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanRun), IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var series = _session.Series!;
        var engine = _session.RequireEngine();
        IsWorking = true;
        _session.IsBusy = true;
        RefreshCommands();
        Rows.Clear();
        var rowByKey = new Dictionary<string, EcuRowViewModel>(StringComparer.OrdinalIgnoreCase);
        Done = Found = FaultTotal = 0;
        Total = series.Ecus.Select(e => e.PrimaryGroup).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        StatusText = $"Scanning {Total} module slots of the {series.Series}…";
        _session.Info(StatusText);

        var progress = new Progress<EcuScanResult>(r =>
        {
            if (!rowByKey.TryGetValue(r.Ecu.Key, out var row))
            {
                row = new EcuRowViewModel(r.Ecu);
                rowByKey[r.Ecu.Key] = row;
                Rows.Add(row);
            }

            row.Result = r;
            if (r.State == EcuScanState.Scanning)
            {
                Current = $"{r.Ecu.Abbreviation} – {r.Ecu.Description}";
            }
            else if (r.State != EcuScanState.Pending)
            {
                Done++;
                Found += r.IsFitted ? 1 : 0;
                FaultTotal += r.FaultList.Count;
            }

            RowsView.Refresh();
        });

        try
        {
            var report = await new QuickTestService(engine).RunAsync(
                series, _session.Vehicle, new QuickTestOptions { ReadIdent = ReadIdent }, progress, cancellationToken);
            _session.LastReport = report;

            // Progress callbacks are posted to the UI thread and may still be queued: take totals from the report.
            Found = report.Fitted.Count();
            FaultTotal = report.FaultCount;
            Done = report.Results.Count;
            StatusText = report.WasCancelled
                ? $"Cancelled after {Done} of {Total}. {Found} modules found, {FaultTotal} faults."
                : $"Done in {report.Duration.TotalSeconds:0} s: {Found} modules found, {FaultTotal} faults.";
            _session.Info(StatusText);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (DiagnosticException ex)
        {
            StatusText = ex.IsInterfaceError ? $"Interface error – check the cable: {ex.Message}" : ex.Message;
            _session.Error(StatusText);
        }
        finally
        {
            Current = null;
            IsWorking = false;
            _session.IsBusy = false;
            RefreshCommands();
        }
    }

    private bool CanClear() => _session.IsConnected && !IsWorking && Rows.Any(r => r.FaultCount > 0);

    [RelayCommand(CanExecute = nameof(CanClear))]
    private async Task ClearAllAsync()
    {
        var withFaults = Rows.Where(r => r.FaultCount > 0 && r.Result is not null).Select(r => r.Result!).ToList();
        if (!_dialogs.Confirm("Clear all fault memories",
                $"Clear the fault memory of {withFaults.Count} module(s)?\n\n" +
                "Export the report first if you need a record. Faults that are still present will come back.\n" +
                "Ignition should be on, engine off."))
        {
            return;
        }

        IsWorking = true;
        RefreshCommands();
        try
        {
            var outcome = await new QuickTestService(_session.RequireEngine()).ClearAllAsync(withFaults);
            var failed = outcome.Where(o => !o.Ok).ToList();
            StatusText = failed.Count == 0
                ? $"Cleared {outcome.Count} fault memories. Run the quick test again to see what comes back."
                : $"Cleared {outcome.Count - failed.Count}, failed: {string.Join(", ", failed.Select(f => $"{f.Ecu.Ecu.Abbreviation} ({f.Message})"))}";
            _session.Info(StatusText);
        }
        catch (DiagnosticException ex)
        {
            StatusText = ex.Message;
            _session.Error(ex.Message);
        }
        finally
        {
            IsWorking = false;
            RefreshCommands();
        }
    }

    private bool HasReport() => _session.LastReport is not null && !IsWorking;

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void ExportHtml() => Export("HTML report|*.html", "html", ReportExporter.ToHtml);

    [RelayCommand(CanExecute = nameof(HasReport))]
    private void ExportCsv() => Export("CSV file|*.csv", "csv", ReportExporter.ToCsv);

    [RelayCommand]
    private void OpenModule(EcuRowViewModel? row)
    {
        row ??= SelectedRow;
        if (row?.Result is not { IsFitted: true } result)
        {
            return;
        }

        _navigator.NavigateTo<ModulesViewModel>().Open(result.Ecu, result.Variant, result.FaultList);
    }

    private void Export(string filter, string extension, Func<QuickTestReport, string> render)
    {
        var report = _session.LastReport!;
        var name = $"bmwDecode_{report.Vehicle.ShortVin ?? report.Series}_{report.Started:yyyyMMdd_HHmm}.{extension}";
        var path = _dialogs.SaveFile("Save quick test report", name, filter);
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path, render(report), System.Text.Encoding.UTF8);
            StatusText = $"Saved {path}";
            if (extension == "html")
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"Could not save the report: {ex.Message}";
        }
    }

    private void RefreshCommands()
    {
        RunCommand.NotifyCanExecuteChanged();
        ClearAllCommand.NotifyCanExecuteChanged();
        ExportHtmlCommand.NotifyCanExecuteChanged();
        ExportCsvCommand.NotifyCanExecuteChanged();
    }
}
