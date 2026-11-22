namespace BmwDecode.App.Services;

/// <summary>Message boxes and file pickers. Async because macOS (Avalonia) dialogs cannot block.</summary>
public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, bool warning = true);

    Task ShowErrorAsync(string title, string message);

    Task<string?> PickFolderAsync(string title, string? initial);

    /// <summary>Asks for a password (masked). Null when cancelled.</summary>
    Task<string?> PromptPasswordAsync(string title, string message);

    /// <summary>Save dialog. <paramref name="filter"/> uses the "Name|*.ext|Name|*.ext" form.</summary>
    Task<string?> SaveFileAsync(string title, string fileName, string filter);
}

/// <summary>The UI thread of the running front end.</summary>
public interface IUiThread
{
    bool CheckAccess();

    void Invoke(Action action);

    void Post(Action action);
}

public interface IClipboardService
{
    /// <summary>False when the clipboard is busy or unavailable.</summary>
    Task<bool> TrySetTextAsync(string text);
}

public interface IThemeService
{
    void Apply(AppTheme theme);
}

/// <summary>A repeating timer that ticks on the UI thread.</summary>
public interface IUiTimer
{
    event EventHandler? Tick;

    void Start();

    void Stop();
}

public interface IUiTimerFactory
{
    IUiTimer Create(TimeSpan interval);
}

/// <summary>Runs everything inline: for tests and for code running before a UI exists.</summary>
public sealed class InlineUiThread : IUiThread
{
    public bool CheckAccess() => true;

    public void Invoke(Action action) => action();

    public void Post(Action action) => action();
}

/// <summary>Parses "Name|*.ext|Name|*.ext" save filters into (name, extensions) pairs for any toolkit.</summary>
public static class FileFilter
{
    public static IReadOnlyList<(string Name, string[] Patterns)> Parse(string? filter)
    {
        var parts = (filter ?? string.Empty).Split('|');
        var list = new List<(string, string[])>();
        for (var i = 0; i + 1 < parts.Length; i += 2)
        {
            list.Add((parts[i].Trim(), parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        }

        return list;
    }
}
