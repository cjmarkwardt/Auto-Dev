using Avalonia.Controls;
using AutoDev.Infrastructure;
using AutoDev.ViewModels.Dialogs;

namespace AutoDev.Views.Dialogs;

public partial class CreateTagDialogWindow : Window
{
    public CreateTagDialogWindow()
    {
        InitializeComponent();
        this.DisableMinimize();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is CreateTagDialogViewModel vm)
        {
            vm.RequestClose += confirmed =>
                Close(confirmed ? new CreateTagDialogResult(vm.FullName.Trim(), vm.Id.Trim()) : null);
        }

        var idBox = this.FindControl<TextBox>("IdBox");
        if (idBox is not null)
        {
            // Only a real keystroke in the Id box itself should stop it from following FullName - the VM's own
            // OnIdChanged fires for both that and FullName-driven programmatic updates, so it can't tell them apart.
            idBox.KeyDown += (_, _) => (DataContext as CreateTagDialogViewModel)?.MarkIdManuallyEdited();
        }

        this.FindControl<TextBox>("FullNameBox")?.Focus();
    }
}
