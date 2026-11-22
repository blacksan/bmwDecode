using BmwDecode.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BmwDecode.App.Services;

/// <summary>
/// Installs the BMW data bundle (bmwDecode-data.zip: Ecu folder + data pack) so a new laptop or Mac needs no INPA or
/// ISTA. On first start with no data, it offers a bundle found on the Desktop / in Downloads, or lets the user pick one;
/// Settings can import a newer bundle at any time. Data goes to the settings folder's Data/.
/// </summary>
public sealed partial class DataService : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly SessionService _session;
    private readonly IDialogService _dialogs;
    private readonly IUiThread _ui;
    private int _running;

    public DataService(SettingsStore store, SessionService session, IDialogService dialogs, IUiThread ui)
    {
        _store = store;
        _session = session;
        _dialogs = dialogs;
        _ui = ui;
    }

    /// <summary>Where imported data lives: Data/sgbd.pack and Data/Ecu.</summary>
    public static string Folder => Path.Combine(SettingsStore.Folder, "Data");

    public static string EcuFolder => Path.Combine(Folder, "Ecu");

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string? _status;

    /// <summary>Raised on the UI thread after new data was installed.</summary>
    public event EventHandler? Updated;

    /// <summary>The installed bundle, for Settings.</summary>
    public string InstalledText => DataPackage.Installed(Folder) is { } m
        ? $"Installed: version {m.Version} ({m.Built.ToLocalTime():yyyy-MM-dd HH:mm}) in {Folder}"
        : "No data file imported yet.";

    /// <summary>First start: with no data pack or no usable ECU folder, offer to import the data file.</summary>
    public async Task StartupAsync(Task packLoad)
    {
        try
        {
            await packLoad.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A failed pack load is logged by the app; importing can still fix it.
        }

        if (ReferenceData.Pack is not null && EcuPathLocator.IsValid(_store.Current.EcuPath))
        {
            return;
        }

        var found = DataPackage.FindBundle();
        string? bundle = null;
        if (found is not null && await _dialogs.ConfirmAsync(
                "Install BMW data",
                $"bmwDecode needs the ECU files and its built-in data. Install them from\n{found}?\n\n" +
                "Choose Cancel to pick another file or to use an ECU folder of your own.",
                warning: false).ConfigureAwait(true))
        {
            bundle = found;
        }

        bundle ??= await _dialogs.OpenFileAsync(
            "Choose bmwDecode-data.zip (Cancel to use an ECU folder of your own)",
            "bmwDecode data|bmwDecode-data*.zip|Zip files|*.zip").ConfigureAwait(true);
        if (bundle is null)
        {
            Status = "No data imported. Import bmwDecode-data.zip in Settings, or choose an ECU folder.";
            return;
        }

        await ImportAsync(bundle).ConfigureAwait(true);
    }

    /// <summary>Settings: pick a data file and import it.</summary>
    public async Task<bool> ChooseAndImportAsync()
    {
        var bundle = await _dialogs.OpenFileAsync("Choose bmwDecode-data.zip", "bmwDecode data|bmwDecode-data*.zip|Zip files|*.zip")
            .ConfigureAwait(true);
        return bundle is not null && await ImportAsync(bundle).ConfigureAwait(true);
    }

    public async Task<bool> ImportAsync(string bundle)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return false;
        }

        IsBusy = true;
        Progress = 0;
        Status = $"Importing {Path.GetFileName(bundle)}…";
        _session.Info($"Data: importing {bundle}");
        var progress = new Progress<DataProgress>(p =>
        {
            Progress = p.Fraction;
            Status = $"{p.Item}: {p.Done / 1048576.0:0} of {p.Total / 1048576.0:0} MB";
        });

        try
        {
            var manifest = await Task.Run(() => DataPackage.Import(bundle, Folder, ReleaseFile, progress)).ConfigureAwait(true);
            if (Directory.Exists(EcuFolder))
            {
                _store.Current.EcuPath = EcuFolder;
                _store.Save();
            }

            ReopenPack();
            Progress = 1;
            Status = $"Installed BMW data version {manifest.Version}. The data file can be deleted now.";
            _session.Info("Data: " + Status);
            OnPropertyChanged(nameof(InstalledText));
            _ui.Post(() => Updated?.Invoke(this, EventArgs.Empty));
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or NotSupportedException)
        {
            Status = $"Import failed: {ex.Message}";
            _session.Error($"Data: {ex.Message}");
            ReopenPack();
            await _dialogs.ShowErrorAsync("Import failed", ex.Message).ConfigureAwait(true);
            return false;
        }
        finally
        {
            IsBusy = false;
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>The open data pack locks sgbd.pack on Windows: close it before the file is replaced.</summary>
    private static void ReleaseFile(string path)
    {
        if (ReferenceData.Pack is { } pack && Path.GetFullPath(pack.Path).Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
        {
            ReferenceData.Pack = null;
            pack.Dispose();
        }
    }

    private void ReopenPack()
    {
        if (ReferenceData.Pack is null)
        {
            try
            {
                ReferenceData.Pack = SgbdPack.TryOpenDefault(Folder);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _session.Error($"Data pack could not be opened: {ex.Message}");
            }
        }
    }
}
