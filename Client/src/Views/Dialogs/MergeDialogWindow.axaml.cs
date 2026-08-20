using Avalonia.Controls;
using AutoDev.Infrastructure;
using AutoDev.ViewModels.Dialogs;

namespace AutoDev.Views.Dialogs;

public partial class MergeDialogWindow : Window
{
    public MergeDialogWindow()
    {
        InitializeComponent();
        this.DisableMinimize();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is MergeDialogViewModel vm)
        {
            vm.RequestClose += confirmed =>
                Close(confirmed && vm.SelectedBranch is { } branch ? new MergeDialogResult(branch, vm.Message.Trim()) : null);
        }
    }
}
