using AutoDev.ViewModels.Dialogs;

namespace AutoDev.ViewModels.Infrastructure;

/// <summary>Seam for anything that needs a native window (folder picker, modal dialogs) so ViewModels stay Avalonia-free.</summary>
public interface IDialogService
{
    /// <summary>startDirectory (if non-null and it still exists) is where the picker opens instead of its own platform default - see HeaderViewModel/IWorkspaceService.GetLastParentFolderAsync.</summary>
    Task<string?> PickFolderAsync(string? startDirectory = null);
    /// <summary>requireValue removes the Cancel button and blocks every other way of dismissing the window (native close button, Escape) - the only way out is confirming OK with a non-blank value. Used where skipping isn't a valid option.</summary>
    Task<string?> ShowInputDialogAsync(string title, string label, string initialValue = "", bool requireValue = false);
    /// <summary>confirmLabel/isDestructive default to the delete-confirmation look (red "Delete" button) that most existing callers want; pass a non-destructive action's own verb (e.g. "Publish") and isDestructive: false for those.</summary>
    Task<bool> ShowConfirmDialogAsync(string title, string message, string confirmLabel = "Delete", bool isDestructive = true);
    /// <summary>Prompts for a Squash base branch and commit message - null if cancelled. See SquashDialogViewModel.</summary>
    Task<SquashDialogResult?> ShowSquashDialogAsync(IReadOnlyList<string> branches, Func<string, Task<string>> defaultMessageProvider);
    /// <summary>Prompts for a Rebase onto-branch and its (always-applied) squash message - null if cancelled. See RebaseDialogViewModel.</summary>
    Task<RebaseDialogResult?> ShowRebaseDialogAsync(IReadOnlyList<string> branches, Func<string, Task<string>> defaultMessageProvider);
    /// <summary>Prompts for a Merge target branch and (possibly-unused, only if more than one commit needs squashing first) squash message - null if cancelled. See MergeDialogViewModel.</summary>
    Task<MergeDialogResult?> ShowMergeDialogAsync(IReadOnlyList<string> branches, Func<string, Task<string>> defaultMessageProvider);
    /// <summary>A single-button ("OK") informational popup - used for a failed git action's error message instead of a persistent inline label. See MessageDialogViewModel.</summary>
    Task ShowMessageDialogAsync(string title, string message);
    /// <summary>Prompts for the git user.name/user.email to configure globally when neither is set yet - null if cancelled. See GitIdentityDialogViewModel.</summary>
    Task<GitIdentityDialogResult?> ShowGitIdentityDialogAsync();
}
