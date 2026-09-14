using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Dialogs;

public sealed partial class ConfirmDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private string title = "";

    [ObservableProperty]
    private string message = "";

    [ObservableProperty]
    private string confirmLabel = "Delete";

    [ObservableProperty]
    private bool isDestructive = true;

    public event Action<bool>? RequestClose;

    [RelayCommand]
    private void Confirm() => RequestClose?.Invoke(true);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}
