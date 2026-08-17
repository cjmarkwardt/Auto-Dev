using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Content;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Sidebar;

public sealed partial class VersionSectionViewModel : ViewModelBase, IDisposable
{
    /// <summary>Safety cap on the rebase-conflict auto-resolution loop, so a conflict Claude can't actually resolve doesn't spin forever - see ResolveRebaseConflictsAsync.</summary>
    private const int MaxConflictResolutionAttempts = 3;

    /// <summary>How often the background remote sync (fetch/prune/non-current-branch reset - see WorkspaceVersioningService.SyncWithRemoteAsync, folded into every RefreshAsync) runs even with no user action.</summary>
    private static readonly TimeSpan PeriodicSyncInterval = TimeSpan.FromSeconds(60);

    private readonly IWorkspaceVersioningService versioningService;
    private readonly IDialogService dialogService;
    private readonly GenerateTabViewModel generate;
    private readonly IUiDispatcher dispatcher;
    private readonly System.Timers.Timer periodicSyncTimer;

    public VersionSectionViewModel(
        IWorkspaceVersioningService versioningService,
        IDialogService dialogService,
        GenerateTabViewModel generate,
        IUiDispatcher dispatcher)
    {
        this.versioningService = versioningService;
        this.dialogService = dialogService;
        this.generate = generate;
        this.dispatcher = dispatcher;
        generate.NormalTurnStarted += OnGenerateNormalTurnStarted;
        generate.NormalTurnCompleted += OnGenerateNormalTurnCompleted;
        generate.HiddenTurnStarted += OnGenerateHiddenTurnStarted;
        generate.HiddenTurnFinished += OnGenerateHiddenTurnFinished;

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

    partial void OnTargetChanged(GitTarget? value)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(HasBranchSubtitle));
        OnPropertyChanged(nameof(IsPublicBranch));
        OnPropertyChanged(nameof(PublicPrivateLabel));
    }

    /// <summary>The big label at the top of the Version section - the branch's human name when resolvable, otherwise the raw ref (branch id, tag name, or short commit hash).</summary>
    public string DisplayName => Target?.Branch?.Name ?? Target?.Ref ?? "";

    /// <summary>Whether the small subtitle (the raw branch id, distinct from its display name) should show - only when targeting a branch with resolvable info.</summary>
    public bool HasBranchSubtitle => Target?.Branch is not null;

    /// <summary>Only meaningful while HasBranchSubtitle is true - see BranchInfo.IsPublic for what public/private actually means.</summary>
    public bool IsPublicBranch => Target?.Branch?.IsPublic ?? false;

    public string PublicPrivateLabel => IsPublicBranch ? "Public" : "Private";

    [ObservableProperty]
    private VersionActionState _actionState = VersionActionState.Empty;

    partial void OnActionStateChanged(VersionActionState value) => OnPropertyChanged(nameof(HasPendingChanges));

    /// <summary>
    /// Aliases ActionState.CanReset rather than tracking its own state - CanReset is already exactly
    /// "does `git status` show any uncommitted changes" (see WorkspaceVersioningService.GetActionStateAsync,
    /// which computes it identically for every target kind), so a separate field would just be the same value
    /// under a clearer name for this specific use (a status indicator, not a button's enabled state).
    /// </summary>
    public bool HasPendingChanges => ActionState.CanReset;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True from the moment a user submits a Generate message until the turn finishes - see OnGenerateNormalTurnStarted/Completed. Drives IsInteractionBlocked, which locks the sidebar sections and (via WorkspaceTabViewModel/WorkspaceContentViewModel) the Edit tab and History tab's controls.</summary>
    [ObservableProperty]
    private bool _isAiWorking;

    [ObservableProperty]
    private string _statusMessage = "";

    /// <summary>Blocks the Version section's own action buttons - true during either a git-only action (IsBusy) or the whole Generate-turn-plus-commit workflow (IsAiWorking).</summary>
    public bool IsInteractionBlocked => IsBusy || IsAiWorking;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(IsInteractionBlocked));
    partial void OnIsAiWorkingChanged(bool value) => OnPropertyChanged(nameof(IsInteractionBlocked));

    /// <summary>Raised whenever the targeted branch/tag/commit changes, at the end of every RefreshAsync.</summary>
    public event Action<GitTarget?>? TargetChanged;

    private void OnGenerateNormalTurnStarted() => IsAiWorking = true;

    /// <summary>Unlocks the sidebar/Edit/History controls once a genuine user-submitted Generate turn finishes - whatever it changed is left as pending, uncommitted changes; the user commits explicitly via the Version section's Commit action, same as any other edit.</summary>
    private void OnGenerateNormalTurnCompleted(bool success) => IsAiWorking = false;

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

    /// <summary>Called once when a workspace tab is opened - silently creates the repo (git init + the "main" branch's base commit) if one doesn't exist yet, then reads the current target and starts the periodic background sync.</summary>
    public async Task EnsureRepoAsync()
    {
        if (!await versioningService.IsRepoInitializedAsync())
        {
            await RunBusyAsync(() => versioningService.InitializeRepoAsync());
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

    /// <summary>Configures or repoints the "origin" remote from the Version section, any time (not just at repo creation) - RunBusyAsync's trailing RefreshAsync re-raises TargetChanged, which is what tells the History tab to reload from the new remote.</summary>
    [RelayCommand]
    private async Task SetRemoteAsync()
    {
        var currentUrl = await versioningService.GetRemoteUrlAsync() ?? "";
        var newUrl = await dialogService.ShowInputDialogAsync("Remote Repository", "Remote URL", currentUrl);
        if (string.IsNullOrWhiteSpace(newUrl) || newUrl.Trim() == currentUrl)
        {
            return;
        }

        await RunBusyAsync(() => versioningService.ConfigureRemoteAsync(newUrl.Trim()));
    }

    [RelayCommand]
    private async Task BranchAsync()
    {
        var result = await dialogService.ShowCreateBranchDialogAsync();
        if (result is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var outcome = await versioningService.CreateBranchAsync(result.Name, result.Id, result.IsPublic);
            if (outcome == BranchCreationOutcome.IdAlreadyExists)
            {
                StatusMessage = $"A branch named \"{result.Id}\" already exists.";
            }
        });
    }

    /// <summary>Creates an annotated tag at the current spot (HEAD) - see CreateTagDialogViewModel for the Full Name/Id prompt and IWorkspaceVersioningService.CreateTagAsync for why both exist (Id is the actual git ref name; Full Name becomes the tag's own message and is what the History tab's timeline shows).</summary>
    [RelayCommand]
    private async Task TagAsync()
    {
        var result = await dialogService.ShowCreateTagDialogAsync();
        if (result is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var outcome = await versioningService.CreateTagAsync(result.Id, result.FullName);
            if (outcome == TagCreationOutcome.IdAlreadyExists)
            {
                StatusMessage = $"A tag named \"{result.Id}\" already exists.";
            }
        });
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!await dialogService.ShowConfirmDialogAsync("Reset", "Discard all pending changes? This cannot be undone.", confirmLabel: "Reset"))
        {
            return;
        }

        await RunBusyAsync(() => versioningService.ResetAsync());
    }

    [RelayCommand]
    private async Task SquashAsync()
    {
        if (!await dialogService.ShowConfirmDialogAsync("Squash", "Combine all pending changes and commits into this branch's base commit?", confirmLabel: "Squash", isDestructive: false))
        {
            return;
        }

        await RunBusyAsync(() => versioningService.SquashAsync());
    }

    [RelayCommand]
    private async Task RebaseAsync()
    {
        await RunBusyAsync(async () =>
        {
            var outcome = await versioningService.RebaseAsync();
            outcome = await ResolveRebaseConflictsAsync(outcome, BuildRebaseConflictInstruction);
            await FinishRebaseAsync(outcome);
        });
    }

    [RelayCommand]
    private async Task MergeAsync()
    {
        await RunBusyAsync(async () =>
        {
            var outcome = await versioningService.RebaseAsync();
            outcome = await ResolveRebaseConflictsAsync(outcome, BuildRebaseConflictInstruction);
            if (await FinishRebaseAsync(outcome))
            {
                await versioningService.FinishMergeAsync();
            }
        });
    }

    /// <summary>Shared tail for Rebase/Merge once the conflict-resolution loop has settled on a final outcome - pushes on success, aborts and reports on unresolved conflicts or failure. Returns whether it succeeded.</summary>
    private async Task<bool> FinishRebaseAsync(RebaseOutcome outcome)
    {
        if (outcome == RebaseOutcome.Succeeded)
        {
            await versioningService.PushCurrentBranchAsync(force: true);
            return true;
        }

        if (outcome == RebaseOutcome.Conflicts)
        {
            await versioningService.AbortRebaseAsync();
            StatusMessage = "Could not automatically resolve the rebase conflicts - aborted.";
        }
        else
        {
            StatusMessage = "Rebase failed.";
        }

        return false;
    }

    private static string BuildRebaseConflictInstruction(IReadOnlyList<string> conflictedFiles) =>
        "The rebase produced merge conflicts in: " + string.Join(", ", conflictedFiles) + ". " +
        "Open each file, resolve the conflict by editing it to the correct final content and removing the " +
        "conflict markers (<<<<<<<, =======, >>>>>>>), then stage the resolved files with `git add`. Reply once " +
        "every conflict is resolved and staged.";

    /// <summary>
    /// Shared conflict-resolution loop for RebaseAsync/MergeAsync - a no-op unless the initial rebase attempt
    /// already came back Conflicts. Locks the sidebar/Edit/History controls via IsAiWorking for the whole
    /// loop, exactly like a normal Generate turn, since Claude is actively editing files here just the same -
    /// only IsBusy (the full-screen overlay) drops out during the actual RunAutomatedTurnAsync call, so the
    /// user can still watch the exchange happen in Generate. Restores IsAiWorking to whatever it was before
    /// (rather than unconditionally clearing it) since this can run nested inside an already-locked flow.
    /// </summary>
    private async Task<RebaseOutcome> ResolveRebaseConflictsAsync(RebaseOutcome outcome, Func<IReadOnlyList<string>, string> buildInstruction)
    {
        if (outcome != RebaseOutcome.Conflicts)
        {
            return outcome;
        }

        var wasAlreadyLocked = IsAiWorking;
        IsAiWorking = true;
        try
        {
            for (var attempt = 0; outcome == RebaseOutcome.Conflicts && attempt < MaxConflictResolutionAttempts; attempt++)
            {
                var conflictedFiles = await versioningService.GetConflictedFilesAsync();
                var instruction = buildInstruction(conflictedFiles);

                // Let the user watch/interact with Generate while Claude resolves the conflict - only the
                // surrounding git-only work (rebasing, checking/continuing) blocks with the loading overlay.
                IsBusy = false;
                try
                {
                    await generate.RunAutomatedTurnAsync(instruction);
                }
                finally
                {
                    IsBusy = true;
                }

                if (await versioningService.HasConflictsAsync())
                {
                    continue; // not actually resolved yet - ask again, within the same attempt budget
                }

                outcome = await versioningService.ContinueRebaseAsync();
            }
        }
        finally
        {
            IsAiWorking = wasAlreadyLocked;
        }

        return outcome;
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        var newName = await dialogService.ShowInputDialogAsync("Rename Branch", "New name", Target?.Branch?.Name ?? "");
        if (string.IsNullOrWhiteSpace(newName))
        {
            return;
        }

        if (!await dialogService.ShowConfirmDialogAsync("Rename Branch", "Renaming will squash all pending changes and commits on this branch into one. Continue?", confirmLabel: "Rename", isDestructive: false))
        {
            return;
        }

        await RunBusyAsync(() => versioningService.RenameAsync(newName.Trim()));
    }

    /// <summary>
    /// Defaults the message box to this branch's own name for a private branch (the common case - one user,
    /// short-lived, "what it's called" is usually a perfectly good commit message) but leaves it empty for a
    /// public one, so a shared/long-lived branch's history can't accidentally end up with a run of commits
    /// all just reading the branch name because the box already had text and Enter was quicker than deleting
    /// it - IsNullOrWhiteSpace below already treats an empty message as "cancelled", so an empty default is
    /// exactly what makes typing a real message required rather than merely encouraged.
    /// </summary>
    [RelayCommand]
    private async Task CommitAsync()
    {
        var defaultMessage = Target?.Branch is { IsPublic: false, Name: { } name } ? name : "";
        var message = await dialogService.ShowInputDialogAsync("Commit", "Message", defaultMessage);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await RunBusyAsync(() => versioningService.CommitAsync(message.Trim()));
    }

    /// <summary>
    /// Set by WorkspaceTabViewModel to flush the Edit tab's pending debounced autosave before every mutating
    /// action. Without this, typing in Edit then immediately triggering a branch action (within the 750ms
    /// autosave debounce) lets the action happen while the edit still only exists in memory - the debounce
    /// then fires afterward and silently writes that stale content onto whatever branch ended up checked out.
    /// See EditTabViewModel.FlushPendingSaveAsync.
    /// </summary>
    public Func<Task>? FlushPendingEditBeforeMutation { get; set; }

    /// <summary>Also called by HistoryTabViewModel for its checkout action, so the loading overlay (bound to IsBusy) covers that too.</summary>
    public async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        StatusMessage = "";
        try
        {
            if (FlushPendingEditBeforeMutation is not null)
            {
                await FlushPendingEditBeforeMutation();
            }

            await action();
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    /// <summary>Re-syncs with the remote, re-reads the current target/action state from git, and re-raises TargetChanged - also called by HistoryTabViewModel after checking out a different commit from the History tab, and periodically by periodicSyncTimer.</summary>
    public async Task RefreshAsync()
    {
        await versioningService.SyncWithRemoteAsync();
        Target = await versioningService.GetCurrentTargetAsync();
        ActionState = await versioningService.GetActionStateAsync();
        TargetChanged?.Invoke(Target);
    }

    public void Dispose()
    {
        periodicSyncTimer.Stop();
        periodicSyncTimer.Dispose();
    }
}
