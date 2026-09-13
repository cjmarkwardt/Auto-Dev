using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Dialogs;

public sealed partial class ConfirmDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private string _confirmLabel = "Delete";

    [ObservableProperty]
    private bool _isDestructive = true;

    public event Action<bool>? RequestClose;

    [RelayCommand]
    private void Confirm() => RequestClose?.Invoke(true);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}
