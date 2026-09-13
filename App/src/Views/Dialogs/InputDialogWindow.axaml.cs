using Avalonia.Controls;
using AutoDev.Infrastructure;
using AutoDev.ViewModels.Dialogs;

namespace AutoDev.Views.Dialogs;

public partial class InputDialogWindow : Window
{
    private bool _closeAllowed;

    public InputDialogWindow()
    {
        InitializeComponent();
        this.DisableMinimize();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is InputDialogViewModel vm)
        {
            vm.RequestClose += confirmed =>
            {
                _closeAllowed = true;
                Close(confirmed ? vm.Value : null);
            };
        }

        this.FindControl<TextBox>("ValueBox")?.Focus();
    }

    // When RequireValue is set, the only sanctioned way out is the OK button's RequestClose above - block
    // the native close button, Escape, and Alt+F4 so a value can't be skipped by dismissing the window instead.
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!_closeAllowed && DataContext is InputDialogViewModel { RequireValue: true })
        {
            e.Cancel = true;
        }
    }
}
