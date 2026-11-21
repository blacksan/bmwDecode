using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Data;
using BmwDecode.App.Services;
using BmwDecode.Core.Catalog;
using BmwDecode.Core.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BmwDecode.App.ViewModels;

/// <summary>Session log: every job, identification and error.</summary>
public sealed partial class LogViewModel : PageViewModel
{
    private readonly SessionService _session;
    private readonly DialogService _dialogs;

    public LogViewModel(SessionService session, DialogService dialogs)
        : base("Log", "\uE81C")
    {
        _session = session;
        _dialogs = dialogs;
        EntriesView = CollectionViewSource.GetDefaultView(session.LogEntries);
        EntriesView.Filter = o => ShowDebug || o is DiagnosticLogEntry { Level: not Core.Diagnostics.LogLevel.Debug };
    }

    public ICollectionView EntriesView { get; }

    [ObservableProperty]
    private bool _showDebug;

    partial void OnShowDebugChanged(bool value) => EntriesView.Refresh();

    [RelayCommand]
    private void Clear() => _session.LogEntries.Clear();

    [RelayCommand]
    private void Copy()
    {
        try
        {
            Clipboard.SetText(Render());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            StatusText = "Clipboard is busy – try again.";
        }
    }

    [RelayCommand]
    private void Save()
    {
        var path = _dialogs.SaveFile("Save session log", $"bmwDecode_{DateTime.Now:yyyyMMdd_HHmm}.log", "Log file|*.log|Text file|*.txt");
        if (path is not null)
        {
            try
            {
                File.WriteAllText(path, Render(), Encoding.UTF8);
                StatusText = $"Saved {path}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusText = $"Could not save: {ex.Message}";
            }
        }
    }

    private string Render()
    {
        var sb = new StringBuilder();
        foreach (var e in _session.LogEntries)
        {
            sb.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture))
              .Append(' ').Append(e.Level.ToString().ToUpperInvariant().PadRight(7)).Append(' ').AppendLine(e.Message);
        }

        return sb.ToString();
    }
}

/// <summary>EDIABAS folder, theme, defaults and about.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly SettingsStore _store;
    private readonly DialogService _dialogs;
    private readonly ThemeService _theme;

    public SettingsViewModel(SettingsStore store, DialogService dialogs, ThemeService theme)
        : base("Settings", "\uE713")
    {
        _store = store;
        _dialogs = dialogs;
        _theme = theme;
        var s = store.Current;
        _ecuPath = s.EcuPath;
        _selectedTheme = s.Theme;
        _liveIntervalMs = s.LiveIntervalMs;
        _ediabasTrace = s.EdiabasTrace;
        _translateTexts = s.TranslateTexts;
        _autoScan = s.AutoScanOnConnect;
        SeriesOptions = [.. VehicleCatalog.Default.ESeries.Select(x => x.Series)];
        _defaultSeries = s.DefaultSeries;
    }

    public IReadOnlyList<AppTheme> Themes { get; } = [AppTheme.System, AppTheme.Light, AppTheme.Dark];
    public IReadOnlyList<string> SeriesOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EcuStatus), nameof(EcuValid))]
    private string _ecuPath;

    [ObservableProperty]
    private AppTheme _selectedTheme;

    [ObservableProperty]
    private int _liveIntervalMs;

    [ObservableProperty]
    private bool _ediabasTrace;

    [ObservableProperty]
    private string _defaultSeries;

    [ObservableProperty]
    private bool _translateTexts;

    [ObservableProperty]
    private bool _autoScan;

    public string TranslationInfo
    {
        get
        {
            var t = Core.Translation.Translator.Current;
            return $"{t.PhraseCount:N0} phrases and {t.WordCount:N0} words built in. Correct any text in translations.tsv " +
                   "(German<TAB>English per line) in the settings folder; restart to apply.";
        }
    }

    public string IstaInfo
    {
        get
        {
            var cars = Core.Vehicle.IstaHistory.Load();
            return cars.Count == 0
                ? "No ISTA session files found. When ISTA has identified a car on this PC, its VIN, type key and model are reused here."
                : $"{cars.Count} car(s) known from ISTA sessions: " + string.Join(", ", cars.Take(5).Select(c => $"{c.Vin[^7..]} {c.Model ?? c.Series}"));
        }
    }

    public bool EcuValid => EcuPathLocator.IsValid(EcuPath);

    public string EcuStatus
    {
        get
        {
            var n = EcuPathLocator.CountSgbd(EcuPath);
            if (n == 0)
            {
                return "No SGBD files (.prg) found here. Use the Ecu folder of your EDIABAS install, e.g. C:\\EDIABAS\\Ecu.";
            }

            var hasFa = File.Exists(Path.Combine(EcuPath, "FA.PRG")) || File.Exists(Path.Combine(EcuPath, "fa.prg"));
            var hasUtility = File.Exists(Path.Combine(EcuPath, "UTILITY.PRG")) || File.Exists(Path.Combine(EcuPath, "utility.prg"));
            var missing = new List<string>();
            if (!hasFa)
            {
                missing.Add("FA.PRG (series decoding)");
            }

            if (!hasUtility)
            {
                missing.Add("UTILITY.PRG (battery/ignition)");
            }

            return $"{n:N0} SGBD files found." + (missing.Count > 0 ? $" Missing: {string.Join(", ", missing)}." : " Ready.");
        }
    }

    public string Version =>
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "dev";

    public string SettingsFolder => SettingsStore.Folder;

    [RelayCommand]
    private void Browse()
    {
        if (_dialogs.PickFolder("Select the EDIABAS Ecu folder (contains *.prg files)", EcuPath) is { } path)
        {
            EcuPath = path;
        }
    }

    [RelayCommand]
    private void Detect()
    {
        if (EcuPathLocator.Detect() is { } path)
        {
            EcuPath = path;
            StatusText = $"Found {path}";
        }
        else
        {
            StatusText = "No EDIABAS install found on C:, D: or E:. Browse to the Ecu folder manually.";
        }
    }

    [RelayCommand]
    private void OpenSettingsFolder()
    {
        Directory.CreateDirectory(SettingsStore.Folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SettingsStore.Folder) { UseShellExecute = true });
    }

    partial void OnEcuPathChanged(string value) => Persist(s => s.EcuPath = value?.Trim() ?? string.Empty);

    partial void OnSelectedThemeChanged(AppTheme value)
    {
        Persist(s => s.Theme = value);
        _theme.Apply(value);
    }

    partial void OnLiveIntervalMsChanged(int value) => Persist(s => s.LiveIntervalMs = Math.Clamp(value, 100, 10_000));

    partial void OnEdiabasTraceChanged(bool value) => Persist(s => s.EdiabasTrace = value);

    partial void OnDefaultSeriesChanged(string value) => Persist(s => s.DefaultSeries = value);

    partial void OnTranslateTextsChanged(bool value)
    {
        Persist(s => s.TranslateTexts = value);
        Core.Translation.Translator.Current.Enabled = value;
    }

    partial void OnAutoScanChanged(bool value) => Persist(s => s.AutoScanOnConnect = value);

    private void Persist(Action<AppSettings> change)
    {
        change(_store.Current);
        if (!_store.Save())
        {
            StatusText = $"Settings could not be saved to {SettingsStore.Folder}.";
        }
    }
}
