using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace BmwDecode.App.Services;

/// <summary>WPF message boxes and pickers behind <see cref="IDialogService"/> (they complete synchronously).</summary>
public sealed class DialogService : IDialogService
{
    public Task<bool> ConfirmAsync(string title, string message, bool warning = true) =>
        Task.FromResult(Confirm(title, message, warning));

    public Task ShowErrorAsync(string title, string message)
    {
        ShowError(title, message);
        return Task.CompletedTask;
    }

    public Task<string?> PickFolderAsync(string title, string? initial) => Task.FromResult(PickFolder(title, initial));

    public Task<string?> OpenFileAsync(string title, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        var owner = Application.Current?.MainWindow;
        var ok = owner is { IsLoaded: true } ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        return Task.FromResult(ok == true ? dialog.FileName : null);
    }

    public Task<string?> SaveFileAsync(string title, string fileName, string filter) =>
        Task.FromResult(SaveFile(title, fileName, filter));

    public bool Confirm(string title, string message, bool warning = true) =>
        Show(message, title, MessageBoxButton.OKCancel, warning ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.Cancel)
            == MessageBoxResult.OK;

    public void ShowError(string title, string message) =>
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error, MessageBoxResult.OK);

    private static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult) =>
        Owner is { } owner
            ? MessageBox.Show(owner, message, title, buttons, image, defaultResult)
            : MessageBox.Show(message, title, buttons, image, defaultResult);

    public string? PickFolder(string title, string? initial)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return (Owner is { } o ? dialog.ShowDialog(o) : dialog.ShowDialog()) == true ? dialog.FolderName : null;
    }

    public string? SaveFile(string title, string fileName, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = fileName, Filter = filter, AddExtension = true };
        return (Owner is { } o ? dialog.ShowDialog(o) : dialog.ShowDialog()) == true ? dialog.FileName : null;
    }

    private static Window? Owner => Application.Current?.MainWindow;
}
