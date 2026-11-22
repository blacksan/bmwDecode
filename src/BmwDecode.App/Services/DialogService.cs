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

    public Task<string?> PromptPasswordAsync(string title, string message)
    {
        var owner = Application.Current?.MainWindow;
        var box = new System.Windows.Controls.PasswordBox { Margin = new Thickness(0, 12, 0, 16), MinWidth = 300 };
        var ok = new System.Windows.Controls.Button { Content = "Download", IsDefault = true, MinWidth = 100, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "Cancel", IsCancel = true, MinWidth = 100 };
        if (Application.Current?.TryFindResource("AccentButtonStyle") is Style accent)
        {
            ok.Style = accent;
        }

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var window = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner is { IsLoaded: true } ? owner : null,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => box.Focus();
        return Task.FromResult(window.ShowDialog() == true && box.Password.Length > 0 ? box.Password : null);
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
