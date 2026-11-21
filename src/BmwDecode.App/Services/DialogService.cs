using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace BmwDecode.App.Services;

/// <summary>Thin wrapper around WPF dialogs so view models stay testable.</summary>
public sealed class DialogService
{
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
