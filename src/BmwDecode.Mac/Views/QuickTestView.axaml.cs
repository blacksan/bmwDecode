using Avalonia.Controls;
using Avalonia.Input;
using BmwDecode.App.ViewModels;

namespace BmwDecode.Mac.Views;

public partial class QuickTestView : UserControl
{
    public QuickTestView() => InitializeComponent();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is QuickTestViewModel vm && vm.OpenModuleCommand.CanExecute(vm.SelectedRow))
        {
            vm.OpenModuleCommand.Execute(vm.SelectedRow);
        }
    }
}
