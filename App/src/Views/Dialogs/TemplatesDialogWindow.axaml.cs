using Avalonia.Controls;
using AutoDev.Infrastructure;
using AutoDev.ViewModels.Dialogs;

namespace AutoDev.Views.Dialogs;

public partial class TemplatesDialogWindow : Window
{
    public TemplatesDialogWindow()
    {
        InitializeComponent();
        this.DisableMinimize();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is TemplatesDialogViewModel vm)
        {
            vm.RequestClose += applied => Close(applied);
        }
    }
}
