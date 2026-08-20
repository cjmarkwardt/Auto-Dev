using Avalonia.Controls;
using AutoDev.Infrastructure;
using AutoDev.ViewModels.Dialogs;

namespace AutoDev.Views.Dialogs;

public partial class MessageDialogWindow : Window
{
    public MessageDialogWindow()
    {
        InitializeComponent();
        this.DisableMinimize();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is MessageDialogViewModel vm)
        {
            vm.RequestClose += () => Close();
        }
    }
}
