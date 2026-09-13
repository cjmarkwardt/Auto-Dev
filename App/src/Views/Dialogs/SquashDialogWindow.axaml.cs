using Avalonia.Controls;
using AutoDev.Infrastructure;
using AutoDev.ViewModels.Dialogs;

namespace AutoDev.Views.Dialogs;

public partial class SquashDialogWindow : Window
{
    public SquashDialogWindow()
    {
        InitializeComponent();
        this.DisableMinimize();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is SquashDialogViewModel vm)
        {
            vm.RequestClose += confirmed =>
                Close(confirmed && vm.SelectedBranch is { } branch ? new SquashDialogResult(branch, vm.Message.Trim()) : null);
        }
    }
}
