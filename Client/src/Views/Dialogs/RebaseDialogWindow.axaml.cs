using Avalonia.Controls;
using AutoDev.Infrastructure;
using AutoDev.ViewModels.Dialogs;

namespace AutoDev.Views.Dialogs;

public partial class RebaseDialogWindow : Window
{
    public RebaseDialogWindow()
    {
        InitializeComponent();
        this.DisableMinimize();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is RebaseDialogViewModel vm)
        {
            vm.RequestClose += confirmed =>
                Close(confirmed && vm.SelectedBranch is { } branch ? new RebaseDialogResult(branch, vm.SquashMessage.Trim()) : null);
        }
    }
}
