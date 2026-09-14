using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Dialogs;

public sealed partial class InputDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private string title = "";

    [ObservableProperty]
    private string label = "";

    [ObservableProperty]
    private string value = "";

    /// <summary>When true, the window hides Cancel and blocks every other way of dismissing itself (native close button, Escape) - see InputDialogWindow's Closing handler. OK also stays disabled while Value is blank, so the only way out is confirming a real value.</summary>
    [ObservableProperty]
    private bool requireValue;

    public bool CanConfirm => !RequireValue || !string.IsNullOrWhiteSpace(Value);

    public event Action<bool>? RequestClose;

    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    [RelayCommand]
    private void Ok() => RequestClose?.Invoke(true);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}
