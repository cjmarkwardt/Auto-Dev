using Avalonia.Controls;
using AutoDev.Infrastructure;
using AutoDev.ViewModels.Dialogs;

namespace AutoDev.Views.Dialogs;

public partial class GitIdentityDialogWindow : Window
{
    public GitIdentityDialogWindow()
    {
        InitializeComponent();
        this.DisableMinimize();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is GitIdentityDialogViewModel vm)
        {
            vm.RequestClose += confirmed =>
                Close(confirmed ? new GitIdentityDialogResult(vm.Name.Trim(), vm.Email.Trim()) : null);
        }

        this.FindControl<TextBox>("NameBox")?.Focus();
    }
}
