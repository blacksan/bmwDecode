using BmwDecode.Core.Data;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BmwDecode.App.Services;

/// <summary>
/// Keeps this install's BMW data (Ecu folder + data pack) in sync with the owner's private data server, so a fresh
/// laptop or Mac needs no INPA or ISTA: the first start asks for the server password once, downloads everything into
/// the settings folder (Data/), and later starts only fetch what changed.
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

    /// <summary>Where downloaded data lives: Data/sgbd.pack and Data/Ecu.</summary>
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

    public bool HasLocalData => File.Exists(Path.Combine(Folder, SgbdPack.FileName)) && Directory.Exists(EcuFolder);

    /// <summary>
    /// Start-up: if this install has no BMW data of its own (no ECU folder and no data pack), ask for the password and
    /// download it; otherwise, with a saved password, quietly fetch whatever changed on the server.
    /// </summary>
    public async Task StartupAsync(Task packLoad)
    {
        try
        {
            await packLoad.ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A failed pack load is logged by the app; the download can still fix it.
        }

        var s = _store.Current;
        var missing = ReferenceData.Pack is null || !EcuPathLocator.IsValid(s.EcuPath);
        if (missing)
        {
            await DownloadAsync(interactive: true).ConfigureAwait(true);
        }
        else if (s.DataAutoUpdate && !string.IsNullOrEmpty(s.DataPassword) && HasLocalData)
        {
            await DownloadAsync(interactive: false).ConfigureAwait(true);
        }
    }

    /// <summary>Downloads or updates the data. Interactive: asks for the password when none is saved or it is refused.</summary>
    public async Task<bool> DownloadAsync(bool interactive)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return false;
        }

        try
        {
            var server = string.IsNullOrWhiteSpace(_store.Current.DataServer) ? DataDownloader.DefaultServer : _store.Current.DataServer.Trim();
            var password = _store.Current.DataPassword;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (string.IsNullOrEmpty(password))
                {
                    if (!interactive)
                    {
                        return false;
                    }

                    password = await _dialogs.PromptPasswordAsync(
                        "Download BMW data",
                        $"bmwDecode needs the ECU files and the built-in data pack. Enter the password for {new Uri(server).Host} " +
                        "to download them (about 250–400 MB, once). Cancel to use an ECU folder of your own instead.").ConfigureAwait(true);
                    if (string.IsNullOrEmpty(password))
                    {
                        Status = "Data download skipped. Choose an ECU folder in Settings, or download the data later.";
                        return false;
                    }
                }

                try
                {
                    return await RunAsync(server, password).ConfigureAwait(true);
                }
                catch (UnauthorizedAccessException)
                {
                    password = null;
                    _store.Current.DataPassword = null;
                    _store.Save();
                    Status = "Wrong password.";
                    if (!interactive)
                    {
                        return false;
                    }
                }
            }

            return false;
        }
        finally
        {
            IsBusy = false;
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<bool> RunAsync(string server, string password)
    {
        IsBusy = true;
        Progress = 0;
        Status = $"Checking {new Uri(server).Host}…";
        _session.Info($"Data: checking {server}");

        var progress = new Progress<DataProgress>(p =>
        {
            Progress = p.Fraction;
            Status = p.Total > 0
                ? $"Downloading {p.Item}: {p.Done / 1048576.0:0} of {p.Total / 1048576.0:0} MB"
                : p.Item;
        });

        DataSyncResult result;
        try
        {
            using var downloader = new DataDownloader(server, password);
            result = await Task.Run(() => downloader.SyncAsync(Folder, ReleaseFile, progress)).ConfigureAwait(true);
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException
                                       or UriFormatException or System.Text.Json.JsonException)
        {
            Status = $"Data download failed: {ex.Message}";
            _session.Error($"Data: {ex.Message}");
            ReopenPack();
            return false;
        }

        // The password worked: keep it, and point the app at the downloaded ECU folder.
        _store.Current.DataPassword = password;
        if (Directory.Exists(EcuFolder))
        {
            _store.Current.EcuPath = EcuFolder;
        }

        _store.Save();
        ReopenPack();

        Status = result.Updated.Count == 0
            ? $"BMW data is up to date (version {result.Manifest.Version})."
            : $"Installed BMW data version {result.Manifest.Version} ({string.Join(", ", result.Updated)}).";
        _session.Info("Data: " + Status);
        Progress = 1;
        _ui.Post(() => Updated?.Invoke(this, EventArgs.Empty));
        return true;
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
