using Avalonia.Controls;
using AutoDev.Infrastructure;
using AutoDev.ViewModels.Dialogs;

namespace AutoDev.Views.Dialogs;

public partial class ConfirmDialogWindow : Window
{
    public ConfirmDialogWindow()
    {
        InitializeComponent();
        this.DisableMinimize();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is ConfirmDialogViewModel vm)
        {
            vm.RequestClose += confirmed => Close(confirmed);
        }
    }
}
