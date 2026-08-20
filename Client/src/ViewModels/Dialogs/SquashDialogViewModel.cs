using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Dialogs;

public sealed record SquashDialogResult(string BaseBranch, string Message);

/// <summary>
/// Squash's own base-branch-pick + message prompt - see VersionSectionViewModel.SquashCommand. Branches is
/// already filtered to eligible bases by the caller (IWorkspaceVersioningService.GetEligibleBaseBranchesAsync);
/// messageProvider computes the default commit message for whichever branch is currently selected (an async git
/// lookup - see IWorkspaceVersioningService.GetDefaultSquashMessageAsync), re-run every time the selection changes.
/// </summary>
public sealed partial class SquashDialogViewModel : ViewModelBase
{
    private readonly Func<string, Task<string>> messageProvider;

    public SquashDialogViewModel(IReadOnlyList<string> branches, Func<string, Task<string>> messageProvider)
    {
        Branches = branches;
        this.messageProvider = messageProvider;
        SelectedBranch = branches.FirstOrDefault();
    }

    public IReadOnlyList<string> Branches { get; }

    [ObservableProperty]
    private string? _selectedBranch;

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _isLoadingMessage;

    public bool CanConfirm => SelectedBranch is not null && Message.Trim().Length > 0;

    public event Action<bool>? RequestClose;

    partial void OnSelectedBranchChanged(string? value)
    {
        OnPropertyChanged(nameof(CanConfirm));
        _ = LoadDefaultMessageAsync(value);
    }

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    private async Task LoadDefaultMessageAsync(string? branch)
    {
        if (branch is null)
        {
            Message = "";
            return;
        }

        IsLoadingMessage = true;
        Message = await messageProvider(branch);
        IsLoadingMessage = false;
    }

    [RelayCommand]
    private void Ok() => RequestClose?.Invoke(true);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}
