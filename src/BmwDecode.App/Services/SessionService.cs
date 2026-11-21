using System.Collections.ObjectModel;
using System.Windows;
using BmwDecode.Core.Catalog;
using BmwDecode.Core.Diagnostics;
using BmwDecode.Core.Services;
using BmwDecode.Core.Simulation;
using BmwDecode.Core.Vehicle;
using BmwDecode.Ediabas;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BmwDecode.App.Services;

/// <summary>
/// The diagnostic session shared by every page: active engine, identified vehicle, chosen chassis profile,
/// last quick test and the session log.
/// </summary>
public sealed partial class SessionService : ObservableObject
{
    private const int MaxLogEntries = 5000;
    private readonly SettingsStore _settings;
    private SgbdInspector? _fileInspector;
    private string? _fileInspectorPath;
    private readonly SimulatedEngine _demoInspector = new(fast: true);
    private TypeKeyDatabase? _typeKeys;
    private string? _typeKeysStamp;

    public SessionService(SettingsStore settings)
    {
        _settings = settings;
        _series = VehicleCatalog.Default.Find(settings.Current.DefaultSeries) ?? VehicleCatalog.Default.Find("E84");
    }

    public VehicleCatalog Catalog => VehicleCatalog.Default;

    public ObservableCollection<DiagnosticLogEntry> LogEntries { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConnected), nameof(IsDemo), nameof(EngineName))]
    private IDiagnosticEngine? _engine;

    [ObservableProperty]
    private VehicleIdentity _vehicle = VehicleIdentity.Unknown;

    [ObservableProperty]
    private SupplyStatus? _supply;

    [ObservableProperty]
    private SeriesDefinition? _series;

    [ObservableProperty]
    private QuickTestReport? _lastReport;

    [ObservableProperty]
    private bool _isBusy;

    private int _busyCount;

    /// <summary>
    /// Marks the bus as in use (quick test, live data, module jobs, identification, job explorer) so background
    /// polling of battery/ignition never slips in between related jobs. Dispose to release; scopes nest.
    /// </summary>
    public IDisposable BeginBusy()
    {
        var dispatcher = Application.Current?.Dispatcher;
        void Update(int delta)
        {
            _busyCount = Math.Max(0, _busyCount + delta);
            IsBusy = _busyCount > 0;
        }

        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Update(+1);
        }
        else
        {
            dispatcher.Invoke(() => Update(+1));
        }

        return new BusyScope(() =>
        {
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                Update(-1);
            }
            else
            {
                dispatcher.BeginInvoke(() => Update(-1));
            }
        });
    }

    private sealed class BusyScope(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    [ObservableProperty]
    private string _busyText = string.Empty;

    public bool IsConnected => Engine?.IsConnected == true;
    public bool IsDemo => Engine?.IsSimulation == true;
    public string EngineName => Engine?.Name ?? "Not connected";

    /// <summary>Raised after connect/disconnect so pages can reset state.</summary>
    public event EventHandler? ConnectionChanged;

    /// <summary>Metadata source for the job explorer: the EDIABAS ECU folder if configured, else the demo car.</summary>
    public ISgbdInspector FileInspector
    {
        get
        {
            var path = _settings.Current.EcuPath;
            if (EcuPathLocator.IsValid(path))
            {
                if (_fileInspector is null || !string.Equals(_fileInspectorPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _fileInspector?.Dispose();
                    _fileInspector = new SgbdInspector(path);
                    _fileInspectorPath = path;
                }

                return _fileInspector;
            }

            return Engine as ISgbdInspector ?? _demoInspector;
        }
    }

    /// <summary>Metadata for an ECU's live-data job list: the demo car describes itself, real cars use the SGBD files.</summary>
    public ISgbdInspector InspectorFor(IDiagnosticEngine? engine) =>
        engine is ISgbdInspector simulated ? simulated : FileInspector;

    /// <summary>User-maintained type key list (VIN positions 4–7 → model). Merged over the FZGIDENT tables.</summary>
    public static string TypeKeyCsvPath => System.IO.Path.Combine(SettingsStore.Folder, "typekeys.csv");

    /// <summary>
    /// Type key database from the EDIABAS folder's FZGIDENT.PRG plus typekeys.csv; reloaded when either changes.
    /// Falls back to the demo car's table when no EDIABAS folder is configured.
    /// </summary>
    public async Task<TypeKeyDatabase> GetTypeKeysAsync(CancellationToken cancellationToken = default)
    {
        var inspector = EcuPathLocator.IsValid(_settings.Current.EcuPath) ? FileInspector : (ISgbdInspector)_demoInspector;
        var csvStamp = System.IO.File.Exists(TypeKeyCsvPath) ? System.IO.File.GetLastWriteTimeUtc(TypeKeyCsvPath).Ticks : 0;
        var istaStamp = IstaStamp();
        var stamp = $"{_settings.Current.EcuPath}|{inspector.GetType().Name}|{csvStamp}|{istaStamp}";
        if (_typeKeys is null || _typeKeysStamp != stamp)
        {
            // Cars ISTA identified on this PC (plain XML session files) teach us VINs and type keys.
            var ista = await Task.Run(() => IstaHistory.Load(), cancellationToken);
            _typeKeys = await TypeKeyDatabase.LoadAsync(inspector, TypeKeyCsvPath, cancellationToken, ista);
            _typeKeysStamp = stamp;
        }

        return _typeKeys;
    }

    private static long IstaStamp()
    {
        long newest = 0;
        foreach (var folder in IstaHistory.DefaultFolders().Where(System.IO.Directory.Exists))
        {
            try
            {
                newest = Math.Max(newest, System.IO.Directory.GetLastWriteTimeUtc(folder).Ticks);
            }
            catch (Exception)
            {
                // unreadable folder: ignore
            }
        }

        return newest;
    }

    public async Task ConnectAsync(ConnectionSettings settings, CancellationToken cancellationToken)
    {
        await DisconnectAsync();

        IDiagnosticEngine engine = settings.Kind == InterfaceKind.Demo ? new SimulatedEngine() : new EdiabasEngine();
        engine.Log += OnEngineLog;
        try
        {
            await engine.ConnectAsync(settings, cancellationToken);
        }
        catch
        {
            engine.Log -= OnEngineLog;
            await engine.DisposeAsync();
            throw;
        }

        Engine = engine;
        Vehicle = VehicleIdentity.Unknown;
        LastReport = null;
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DisconnectAsync()
    {
        if (Engine is { } engine)
        {
            Engine = null;
            try
            {
                await engine.DisconnectAsync();
            }
            finally
            {
                engine.Log -= OnEngineLog;
                await engine.DisposeAsync();
            }

            Supply = null;
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IDiagnosticEngine RequireEngine() =>
        Engine is { IsConnected: true } e ? e : throw new DiagnosticException("Connect to the vehicle (or the demo car) first.", "BMWDECODE_NOT_CONNECTED");

    public void Info(string message) => Add(DiagnosticLogEntry.Now(Core.Diagnostics.LogLevel.Info, message));

    public void Warn(string message) => Add(DiagnosticLogEntry.Now(Core.Diagnostics.LogLevel.Warning, message));

    public void Error(string message) => Add(DiagnosticLogEntry.Now(Core.Diagnostics.LogLevel.Error, message));

    private void OnEngineLog(object? sender, DiagnosticLogEntry entry) => Add(entry);

    private void Add(DiagnosticLogEntry entry)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            AddOnUi(entry);
        }
        else
        {
            dispatcher.BeginInvoke(() => AddOnUi(entry));
        }
    }

    private void AddOnUi(DiagnosticLogEntry entry)
    {
        LogEntries.Add(entry);
        while (LogEntries.Count > MaxLogEntries)
        {
            LogEntries.RemoveAt(0);
        }
    }
}
