using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Dialogs;

/// <summary>A single-button ("OK") informational popup - see IDialogService.ShowMessageDialogAsync, used for a failed git action's error message instead of a persistent inline label.</summary>
public sealed partial class MessageDialogViewModel : ViewModelBase
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    public event Action? RequestClose;

    [RelayCommand]
    private void Ok() => RequestClose?.Invoke();
}
