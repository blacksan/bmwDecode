using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using BmwDecode.Core.Diagnostics;

namespace BmwDecode.App.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// User settings, stored as JSON in %LOCALAPPDATA%\bmwDecode\settings.json (Windows) or
/// ~/Library/Application Support/bmwDecode/settings.json (macOS).
/// </summary>
public sealed class AppSettings
{
    public InterfaceKind Interface { get; set; } = InterfaceKind.KDcanSerial;
    public string Port { get; set; } = OperatingSystem.IsWindows() ? "COM1" : string.Empty;
    public string EcuPath { get; set; } = string.Empty;
    public string DefaultSeries { get; set; } = "E84";
    public AppTheme Theme { get; set; } = AppTheme.System;
    public int LiveIntervalMs { get; set; } = 500;
    public bool EdiabasTrace { get; set; }
    public bool ReadIdentInQuickTest { get; set; } = true;

    /// <summary>Translate German SGBD texts to English (offline tables).</summary>
    public bool TranslateTexts { get; set; } = true;

    /// <summary>After connecting: identify the car from its VIN and scan the modules it reports.</summary>
    public bool AutoScanOnConnect { get; set; } = true;

    /// <summary>Private server holding the Ecu folder and data pack (downloaded on first start).</summary>
    public string DataServer { get; set; } = BmwDecode.Core.Data.DataDownloader.DefaultServer;

    /// <summary>The data server password, kept after the first successful download so updates need no prompt.</summary>
    public string? DataPassword { get; set; }

    /// <summary>Check the data server for newer data at start-up (only downloads what changed).</summary>
    public bool DataAutoUpdate { get; set; } = true;
    public Dictionary<string, List<string>> LiveSelections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bmwDecode");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), s_json) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // A corrupt settings file must never stop the app from starting.
            Current = new AppSettings();
        }

        // JSON nulls overwrite defaults; normalise so the rest of the app can trust these values.
        Current.Port ??= OperatingSystem.IsWindows() ? "COM1" : string.Empty;
        Current.EcuPath ??= string.Empty;
        Current.DefaultSeries ??= "E84";
        if (!Enum.IsDefined(Current.Interface))
        {
            Current.Interface = InterfaceKind.KDcanSerial;
        }

        Current.LiveSelections = new Dictionary<string, List<string>>(
            Current.LiveSelections ?? new Dictionary<string, List<string>>(), StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(Current.EcuPath))
        {
            Current.EcuPath = EcuPathLocator.Detect() ?? string.Empty;
        }

        return Current;
    }

    /// <summary>Saves settings; returns false (never throws) if the file cannot be written.</summary>
    public bool Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, s_json));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Finds an existing EDIABAS ECU folder (INPA / ISTA / Rheingold installs).</summary>
public static class EcuPathLocator
{
    public static string? Detect()
    {
        var candidates = new List<string>
        {
            // Installed by the bmwDecode setup (bundled or copied from INPA/ISTA): self-contained, preferred.
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "bmwDecode", "Ecu"),
            Path.Combine(AppContext.BaseDirectory, "Ecu"),
            // macOS: ~/Library/Application Support/bmwDecode/Ecu (copy the Ecu folder there from the Windows PC).
            Path.Combine(SettingsStore.Folder, "Ecu"),
        };

        // EDIABAS sets EDIABAS_PATH / adds its Bin folder to PATH on install.
        if (Environment.GetEnvironmentVariable("EDIABAS_PATH") is { Length: > 0 } ediabasPath)
        {
            candidates.Add(Path.Combine(ediabasPath, "Ecu"));
        }

        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (p.TrimEnd('\\', '/').EndsWith(Path.Combine("EDIABAS", "Bin"), StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(Path.Combine(Path.GetDirectoryName(p.TrimEnd('\\', '/'))!, "Ecu"));
            }
        }

        foreach (var drive in OperatingSystem.IsWindows() ? new[] { "C:", "D:", "E:" } : [])
        {
            candidates.Add($@"{drive}\EDIABAS\Ecu");
            candidates.Add($@"{drive}\EC-Apps\ISTA\Ecu");
            candidates.Add($@"{drive}\Program Files\BMW\ISPI\TRIC\ISTA\Ecu");
            candidates.Add($@"{drive}\Rheingold\Ecu");
            candidates.Add($@"{drive}\ISTA\Ecu");
        }

        return candidates.FirstOrDefault(IsValid);
    }

    public static bool IsValid(string? path) => CountSgbd(path) > 0;

    public static int CountSgbd(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(path, "*.prg").Take(5000).Count();
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
