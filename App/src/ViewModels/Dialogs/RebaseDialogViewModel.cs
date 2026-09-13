using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Dialogs;

public sealed record RebaseDialogResult(string OntoBranch, string SquashMessage);

/// <summary>
/// Rebase's own onto-branch-pick + squash-message prompt - see VersionSectionViewModel.RebaseCommand. Branches
/// and messageProvider follow the same rules as SquashDialogViewModel (same eligible-base filtering, same
/// default message lookup). No auto-squash toggle: a rebase always squashes the current branch's own commits
/// first (see IWorkspaceVersioningService.RebaseWithSquashAsync's own doc comment for why), so this is the
/// same shape as SquashDialogViewModel with a different confirm verb.
/// </summary>
public sealed partial class RebaseDialogViewModel : ViewModelBase
{
    private readonly Func<string, Task<string>> messageProvider;

    public RebaseDialogViewModel(IReadOnlyList<string> branches, Func<string, Task<string>> messageProvider)
    {
        Branches = branches;
        this.messageProvider = messageProvider;
        SelectedBranch = branches.FirstOrDefault();
    }

    public IReadOnlyList<string> Branches { get; }

    [ObservableProperty]
    private string? _selectedBranch;

    [ObservableProperty]
    private string _squashMessage = "";

    [ObservableProperty]
    private bool _isLoadingMessage;

    public bool CanConfirm => SelectedBranch is not null && SquashMessage.Trim().Length > 0;

    public event Action<bool>? RequestClose;

    partial void OnSelectedBranchChanged(string? value)
    {
        OnPropertyChanged(nameof(CanConfirm));
        _ = LoadDefaultMessageAsync(value);
    }

    partial void OnSquashMessageChanged(string value) => OnPropertyChanged(nameof(CanConfirm));

    private async Task LoadDefaultMessageAsync(string? branch)
    {
        if (branch is null)
        {
            SquashMessage = "";
            return;
        }

        IsLoadingMessage = true;
        SquashMessage = await messageProvider(branch);
        IsLoadingMessage = false;
    }

    [RelayCommand]
    private void Ok() => RequestClose?.Invoke(true);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}
