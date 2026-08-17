using Avalonia.Controls;
using AutoDev.Infrastructure;
using AutoDev.ViewModels.Dialogs;

namespace AutoDev.Views.Dialogs;

public partial class CreateBranchDialogWindow : Window
{
    public CreateBranchDialogWindow()
    {
        InitializeComponent();
        this.DisableMinimize();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is CreateBranchDialogViewModel vm)
        {
            vm.RequestClose += confirmed =>
                Close(confirmed ? new CreateBranchDialogResult(vm.Name.Trim(), vm.Id.Trim(), vm.IsPublic) : null);
        }

        var idBox = this.FindControl<TextBox>("IdBox");
        if (idBox is not null)
        {
            // Only a real keystroke in the Id box itself should stop it from following Name - the VM's own
            // OnIdChanged fires for both that and Name-driven programmatic updates, so it can't tell them apart.
            idBox.KeyDown += (_, _) => (DataContext as CreateBranchDialogViewModel)?.MarkIdManuallyEdited();
        }

        this.FindControl<TextBox>("NameBox")?.Focus();
    }
}
