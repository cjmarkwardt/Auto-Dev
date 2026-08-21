using System.Collections.ObjectModel;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Content;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Sidebar;

/// <summary>
/// A passive display of the current git target (branch/tag/commit) and pending-changes state, plus every action
/// that targets the currently checked-out branch directly rather than some other row in the History tab
/// (Commit/Reset/Branch/Tag/Remote/Squash/Rebase/Merge - offered via a click on this section itself, see
/// VersionSectionView) and the shared busy/lock machinery every other mutating git action (triggered from the
/// History tab's own right-click menus - see HistoryTabViewModel) runs through.
/// </summary>
public sealed partial class VersionSectionViewModel : ViewModelBase, IDisposable
{
    /// <summary>Safety cap on the rebase/merge-conflict auto-resolution loop, so a conflict Claude can't actually resolve doesn't spin forever - see ResolveConflictsAsync.</summary>
    private const int MaxConflictResolutionAttempts = 3;

    /// <summary>How often the background remote sync (fetch/prune/non-current-branch reset - see WorkspaceVersioningService.SyncWithRemoteAsync, folded into every RefreshAsync) runs even with no user action.</summary>
    private static readonly TimeSpan PeriodicSyncInterval = TimeSpan.FromSeconds(60);

    private readonly IWorkspaceVersioningService versioningService;
    private readonly IDialogService dialogService;
    private readonly GenerateTabViewModel generate;
    private readonly IUiDispatcher dispatcher;
    private readonly System.Timers.Timer periodicSyncTimer;

    private CancellationTokenSource? busyCts;

    public VersionSectionViewModel(IWorkspaceVersioningService versioningService, IDialogService dialogService, GenerateTabViewModel generate, IUiDispatcher dispatcher)
    {
        this.versioningService = versioningService;
        this.dialogService = dialogService;
        this.generate = generate;
        this.dispatcher = dispatcher;
        generate.NormalTurnStarted += OnGenerateNormalTurnStarted;
        generate.NormalTurnCompleted += OnGenerateNormalTurnCompleted;
        generate.HiddenTurnStarted += OnGenerateHiddenTurnStarted;
        generate.HiddenTurnFinished += OnGenerateHiddenTurnFinished;
        generate.TurnPaused += OnGenerateTurnPaused;
        generate.TurnResumed += OnGenerateTurnResumed;

        periodicSyncTimer = new System.Timers.Timer(PeriodicSyncInterval) { AutoReset = true };
        periodicSyncTimer.Elapsed += (_, _) =>
        {
            if (!IsInteractionBlocked)
            {
                dispatcher.Post(() => _ = RefreshAsync());
            }
        };
    }

    /// <summary>Whatever IsAiWorking was the moment a hidden turn started (see OnGenerateHiddenTurnStarted/Finished) - null while no hidden turn is in flight. Restoring to this, rather than blindly clearing IsAiWorking, keeps the workspace locked afterward when the hidden turn was nested inside an already-locked flow.</summary>
    private bool? _wasAiWorkingBeforeHiddenTurn;

    [ObservableProperty]
    private GitTarget? _target;

    [ObservableProperty]
    private bool _hasPendingChanges;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True from the moment a user submits a Generate message until the turn finishes - see OnGenerateNormalTurnStarted/Completed. Drives IsInteractionBlocked, which locks the sidebar sections and (via WorkspaceTabViewModel/WorkspaceContentViewModel) the Edit tab and History tab's controls.</summary>
    [ObservableProperty]
    private bool _isAiWorking;

    /// <summary>True while the active Generate turn is paused (GenerateTabViewModel.TurnPaused/TurnResumed) - IsAiWorking stays true the whole time too (see OnGenerateNormalTurnStarted/Completed, deliberately not fired around a pause), so the workspace stays exactly as locked as it was while genuinely working; this only distinguishes the bottom status bar's own "AI is paused" text from "AI work in progress…" (see MainShellView.axaml).</summary>
    [ObservableProperty]
    private bool _isAiPaused;

    /// <summary>The current busy action's own live git command log (command lines plus their output) - see RunBusyAsync/GitCommandLogSink. Shown in the busy overlay; cleared at the start of every new action.</summary>
    public ObservableCollection<string> GitOutputLog { get; } = [];

    /// <summary>Blocks every git action triggered from the History tab or this section's own Commit/Reset - true during either a git-only action (IsBusy) or the whole Generate-turn-plus-commit workflow (IsAiWorking).</summary>
    public bool IsInteractionBlocked => IsBusy || IsAiWorking;

    /// <summary>Shared CanExecute for every command below.</summary>
    private bool CanMutate() => !IsInteractionBlocked;

    private void NotifyMutatingCommandsCanExecuteChanged()
    {
        CommitCommand.NotifyCanExecuteChanged();
        ResetCommand.NotifyCanExecuteChanged();
        BranchCommand.NotifyCanExecuteChanged();
        TagCommand.NotifyCanExecuteChanged();
        RemoteCommand.NotifyCanExecuteChanged();
        SquashCommand.NotifyCanExecuteChanged();
        RebaseCommand.NotifyCanExecuteChanged();
        MergeCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInteractionBlocked));
        NotifyMutatingCommandsCanExecuteChanged();
        CancelBusyCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAiWorkingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInteractionBlocked));
        NotifyMutatingCommandsCanExecuteChanged();
    }

    /// <summary>Raised whenever the targeted branch/tag/commit changes, at the end of every RefreshAsync.</summary>
    public event Action<GitTarget?>? TargetChanged;

    private void OnGenerateNormalTurnStarted() => IsAiWorking = true;

    /// <summary>Unlocks the sidebar/Edit/History controls once a genuine user-submitted Generate turn finishes - whatever it changed is left as pending, uncommitted changes; the user commits explicitly via the History tab's Commit action, same as any other edit. Also always clears IsAiPaused - every path that actually ends the turn (a normal completion, or Stop while paused) needs the "paused" text/state gone too, not just the lock itself.</summary>
    private void OnGenerateNormalTurnCompleted(bool success)
    {
        IsAiWorking = false;
        IsAiPaused = false;
    }

    private void OnGenerateTurnPaused() => IsAiPaused = true;

    private void OnGenerateTurnResumed() => IsAiPaused = false;

    /// <summary>Locks the workspace down for a hidden turn exactly like a visible one - see OnGenerateHiddenTurnFinished.</summary>
    private void OnGenerateHiddenTurnStarted()
    {
        _wasAiWorkingBeforeHiddenTurn ??= IsAiWorking;
        IsAiWorking = true;
    }

    private void OnGenerateHiddenTurnFinished()
    {
        IsAiWorking = _wasAiWorkingBeforeHiddenTurn ?? false;
        _wasAiWorkingBeforeHiddenTurn = null;
    }

    /// <summary>Called once when a workspace tab is opened - silently creates the repo (git init + the initial "main" branch) if one doesn't exist yet, then reads the current target and starts the periodic background sync.</summary>
    public async Task EnsureRepoAsync()
    {
        if (!await versioningService.IsRepoInitializedAsync())
        {
            await RunBusyAsync(ct => versioningService.InitializeRepoAsync(ct));
        }
        else
        {
            await RefreshAsync();

            // Catches the local-exclude pattern for a repo that predates it, or a newer AutoDev build adding
            // to it - a no-op (pure .git/info/exclude bookkeeping, outside the working tree) if already present.
            await versioningService.EnsureLocalGitExcludeAsync();
        }

        periodicSyncTimer.Start();
    }

    /// <summary>
    /// Set by WorkspaceTabViewModel to flush the Edit tab's pending debounced autosave before every mutating
    /// action. Without this, typing in Edit then immediately triggering a branch action (within the 750ms
    /// autosave debounce) lets the action happen while the edit still only exists in memory - the debounce
    /// then fires afterward and silently writes that stale content onto whatever branch ended up checked out.
    /// See EditTabViewModel.FlushPendingSaveAsync.
    /// </summary>
    public Func<Task>? FlushPendingEditBeforeMutation { get; set; }

    /// <summary>
    /// Runs a mutating git action with the loading overlay up - also called by HistoryTabViewModel for its
    /// checkout/merge/rebase/delete actions, so every one of them gets the same overlay (live git output log,
    /// Cancel button) and refreshes Target/HasPendingChanges/TargetChanged the same way afterward. Captures a
    /// pre-action snapshot first and, if cancelled (via CancelBusyCommand, below), reverts back to it - action
    /// gets its own CancellationToken to thread into whichever IWorkspaceVersioningService calls it makes, so a
    /// cancel can actually interrupt an in-flight git subprocess (see GitService.RunAsync) rather than just
    /// racing to be first past a check.
    /// </summary>
    public async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        IsBusy = true;
        GitOutputLog.Clear();
        var snapshot = await versioningService.CaptureSnapshotAsync();
        busyCts = new CancellationTokenSource();
        CancelBusyCommand.NotifyCanExecuteChanged();
        GitCommandLogSink.Current = line => GitOutputLog.Add(line);
        try
        {
            if (FlushPendingEditBeforeMutation is not null)
            {
                await FlushPendingEditBeforeMutation();
            }

            await action(busyCts.Token);
        }
        catch (OperationCanceledException)
        {
            GitOutputLog.Add("Cancelled - reverting…");
            await versioningService.RevertToSnapshotAsync(snapshot);
        }
        finally
        {
            GitCommandLogSink.Current = null;
            busyCts.Dispose();
            busyCts = null;
            IsBusy = false;
            CancelBusyCommand.NotifyCanExecuteChanged();
            await RefreshAsync();
        }
    }

    private bool CanCancelBusy() => busyCts is not null;

    /// <summary>The busy overlay's own Cancel button - signals the running action's CancellationToken, which RunBusyAsync's catch block turns into a revert back to the pre-action snapshot.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelBusy))]
    private void CancelBusy() => busyCts?.Cancel();

    /// <summary>
    /// Shared conflict-resolution loop for this section's own Rebase/Merge and HistoryTabViewModel's Merge
    /// Into Current/Rebase Current Onto This - a no-op unless the initial attempt already came back Conflicts.
    /// Locks the sidebar/Edit/History controls via IsAiWorking for the whole loop, exactly like a normal
    /// Generate turn, since Claude is actively editing files here just the same - only IsBusy (the busy
    /// overlay) drops out during the actual RunAutomatedTurnAsync call, so the user can still watch the
    /// exchange happen in Generate (and cancel it from there - the overlay's own Cancel button isn't shown
    /// while IsBusy is down). Restores IsAiWorking to whatever it was before (rather than unconditionally
    /// clearing it) since this can run nested inside an already-locked flow. continueAction is
    /// ContinueRebaseAsync or ContinueMergeAsync, whichever this conflict belongs to.
    /// </summary>
    public async Task<GitOperationOutcome> ResolveConflictsAsync(GitOperationOutcome outcome, Func<CancellationToken, Task<GitOperationOutcome>> continueAction, CancellationToken cancellationToken)
    {
        if (outcome != GitOperationOutcome.Conflicts)
        {
            return outcome;
        }

        var wasAlreadyLocked = IsAiWorking;
        IsAiWorking = true;
        try
        {
            for (var attempt = 0; outcome == GitOperationOutcome.Conflicts && attempt < MaxConflictResolutionAttempts; attempt++)
            {
                var conflictedFiles = await versioningService.GetConflictedFilesAsync(cancellationToken);
                var instruction = BuildConflictInstruction(conflictedFiles);

                // Let the user watch/interact with Generate while Claude resolves the conflict - only the
                // surrounding git-only work (rebasing/merging, checking/continuing) blocks with the loading overlay.
                IsBusy = false;
                try
                {
                    await generate.RunAutomatedTurnAsync(instruction, cancellationToken: cancellationToken);
                }
                finally
                {
                    IsBusy = true;
                }

                if (await versioningService.HasConflictsAsync(cancellationToken))
                {
                    continue; // not actually resolved yet - ask again, within the same attempt budget
                }

                outcome = await continueAction(cancellationToken);
            }
        }
        finally
        {
            IsAiWorking = wasAlreadyLocked;
        }

        return outcome;
    }

    private static string BuildConflictInstruction(IReadOnlyList<string> conflictedFiles) =>
        "This produced merge conflicts in: " + string.Join(", ", conflictedFiles) + ". " +
        "Open each file, resolve the conflict by editing it to the correct final content and removing the " +
        "conflict markers (<<<<<<<, =======, >>>>>>>), then stage the resolved files with `git add`. Reply once " +
        "every conflict is resolved and staged.";

    /// <summary>Re-syncs with the remote, re-reads the current target/pending-changes state from git, and re-raises TargetChanged - also called after checking out a different commit/branch/tag from the History tab, and periodically by periodicSyncTimer.</summary>
    public async Task RefreshAsync()
    {
        await versioningService.SyncWithRemoteAsync();
        Target = await versioningService.GetCurrentTargetAsync();
        HasPendingChanges = await versioningService.HasUncommittedChangesAsync();
        TargetChanged?.Invoke(Target);
    }

    /// <summary>Commits pending changes to whatever's currently checked out - triggered by clicking this section, see VersionSectionView.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task CommitAsync()
    {
        var message = await dialogService.ShowInputDialogAsync("Commit", "Message");
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await RunBusyAsync(ct => versioningService.CommitAsync(message.Trim(), ct));
    }

    /// <summary>Discards pending changes on whatever's currently checked out - triggered by clicking this section, see VersionSectionView.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task ResetAsync()
    {
        if (!await dialogService.ShowConfirmDialogAsync("Reset", "Discard all pending changes? This cannot be undone.", confirmLabel: "Reset"))
        {
            return;
        }

        await RunBusyAsync(ct => versioningService.ResetAsync(ct));
    }

    /// <summary>Creates a new branch at the current target and checks it out.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task BranchAsync()
    {
        var name = await dialogService.ShowInputDialogAsync("Branch", "Branch name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var trimmedName = name.Trim();
        await RunBusyAsync(async ct =>
        {
            var outcome = await versioningService.CreateBranchAsync(trimmedName, "HEAD", ct);
            if (outcome == BranchCreationOutcome.IdAlreadyExists)
            {
                await dialogService.ShowMessageDialogAsync("Branch", $"A branch named \"{trimmedName}\" already exists.");
            }
        });
    }

    /// <summary>Creates an annotated tag (always - never a plain lightweight one, and always with a blank message) at the current target.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task TagAsync()
    {
        var name = await dialogService.ShowInputDialogAsync("Tag", "Tag name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var trimmedName = name.Trim();
        await RunBusyAsync(async ct =>
        {
            var outcome = await versioningService.CreateTagAsync(trimmedName, "HEAD", ct);
            if (outcome == TagCreationOutcome.IdAlreadyExists)
            {
                await dialogService.ShowMessageDialogAsync("Tag", $"A tag named \"{trimmedName}\" already exists.");
            }
        });
    }

    /// <summary>Configures or repoints the "origin" remote.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task RemoteAsync()
    {
        var currentUrl = await versioningService.GetRemoteUrlAsync() ?? "";
        var newUrl = await dialogService.ShowInputDialogAsync("Remote", "Remote URL", currentUrl);
        if (string.IsNullOrWhiteSpace(newUrl) || newUrl.Trim() == currentUrl)
        {
            return;
        }

        await RunBusyAsync(ct => versioningService.ConfigureRemoteAsync(newUrl.Trim(), ct));
    }

    /// <summary>Squashes the current branch's own commits since diverging from a chosen base branch into one - only offered while targeting a branch, see VersionSectionView.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task SquashAsync()
    {
        var branches = await versioningService.GetEligibleBaseBranchesAsync();
        if (branches.Count == 0)
        {
            await dialogService.ShowMessageDialogAsync("Squash", "No other branch to squash against.");
            return;
        }

        var result = await dialogService.ShowSquashDialogAsync(branches, branch => versioningService.GetDefaultSquashMessageAsync(branch));
        if (result is null)
        {
            return;
        }

        await RunBusyAsync(ct => versioningService.SquashAsync(result.BaseBranch, result.Message.Trim(), ct));
    }

    /// <summary>Rebases the current branch onto a chosen branch, always squashing its own commits first - only offered while targeting a branch, see VersionSectionView. Merge conflicts, if any, are handed to Claude via ResolveConflictsAsync.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task RebaseAsync()
    {
        var branches = await versioningService.GetEligibleBaseBranchesAsync();
        if (branches.Count == 0)
        {
            await dialogService.ShowMessageDialogAsync("Rebase", "No other branch to rebase onto.");
            return;
        }

        var result = await dialogService.ShowRebaseDialogAsync(branches, branch => versioningService.GetDefaultSquashMessageAsync(branch));
        if (result is null)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var outcome = await versioningService.RebaseWithSquashAsync(result.OntoBranch, result.SquashMessage.Trim(), ct);
            outcome = await ResolveConflictsAsync(outcome, ct2 => versioningService.ContinueRebaseAsync(ct2), ct);
            if (outcome == GitOperationOutcome.Succeeded)
            {
                await versioningService.PushCurrentBranchAsync(force: true, ct);
            }
            else if (outcome == GitOperationOutcome.Conflicts)
            {
                await versioningService.AbortRebaseAsync(ct);
                await dialogService.ShowMessageDialogAsync("Rebase", "Could not automatically resolve the rebase conflicts - aborted.");
            }
            else
            {
                await dialogService.ShowMessageDialogAsync("Rebase", "Rebase failed.");
            }
        });
    }

    /// <summary>Fast-forward merges the current branch onto a chosen target branch, squashing first if there's more than one commit to bring over - only offered while targeting a branch, see VersionSectionView. Never conflicts (a fast-forward can't) - fails outright if the current branch isn't actually based on the target's own head.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task MergeAsync()
    {
        var branches = await versioningService.GetEligibleMergeTargetBranchesAsync();
        if (branches.Count == 0)
        {
            await dialogService.ShowMessageDialogAsync("Merge", "No branch this branch can be fast-forward merged onto.");
            return;
        }

        var result = await dialogService.ShowMergeDialogAsync(branches, branch => versioningService.GetDefaultSquashMessageAsync(branch));
        if (result is null)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            var succeeded = await versioningService.FastForwardMergeAsync(result.TargetBranch, result.SquashMessage?.Trim(), ct);
            if (!succeeded)
            {
                await dialogService.ShowMessageDialogAsync("Merge", $"'{Target?.BranchName}' isn't based on the head of '{result.TargetBranch}' - can't fast-forward.");
            }
        });
    }

    public void Dispose()
    {
        periodicSyncTimer.Stop();
        periodicSyncTimer.Dispose();
    }
}
