using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Dialogs;

public sealed record MergeDialogResult(string TargetBranch, string? SquashMessage);

/// <summary>
/// Merge's own target-branch-pick + squash-message prompt - see VersionSectionViewModel.MergeCommand. Branches
/// and messageProvider follow the same rules as SquashDialogViewModel/RebaseDialogViewModel; the message is
/// only actually used if the current branch turns out to have more than one commit since diverging from the
/// picked branch (see IWorkspaceVersioningService.FastForwardMergeAsync) - shown unconditionally anyway so
/// there's no separate "will this squash?" check running ahead of the dialog opening.
/// </summary>
public sealed partial class MergeDialogViewModel : ViewModelBase
{
    private readonly Func<string, Task<string>> messageProvider;

    public MergeDialogViewModel(IReadOnlyList<string> branches, Func<string, Task<string>> messageProvider)
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
