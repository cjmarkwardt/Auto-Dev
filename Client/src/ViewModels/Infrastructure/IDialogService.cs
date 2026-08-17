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
    /// <summary>Prompts for a new branch's name/id/public-vs-private - null if cancelled. See CreateBranchDialogViewModel.</summary>
    Task<CreateBranchDialogResult?> ShowCreateBranchDialogAsync();
    /// <summary>Prompts for a new tag's full name/id - null if cancelled. See CreateTagDialogViewModel.</summary>
    Task<CreateTagDialogResult?> ShowCreateTagDialogAsync();
}
