using System.Collections.ObjectModel;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Content;
using AutoDev.ViewModels.Dialogs;
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

    /// <summary>Whether this workspace's own tab is the currently-selected one - see SetActive. Starts true, matching a freshly opened workspace always becoming the selected tab immediately (see WorkspaceFactory/MainShellViewModel.OnWorkspaceOpened).</summary>
    private bool isActive = true;

    /// <summary>Set only once the current busy action has failed (see MarkFailed) - RunBusyAsync awaits this instead of closing the overlay immediately; ConfirmBusyCommand completes it once the user has actually seen GitOutputLog and dismisses it themselves.</summary>
    private TaskCompletionSource? busyConfirmTcs;

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

        // GitOutputLogText exists purely so the busy overlay can bind one SelectableTextBlock to the whole
        // log as a single selectable/copyable block, rather than one plain (unselectable) TextBlock per line
        // via an ItemsControl - see WorkspaceView.axaml.
        GitOutputLog.CollectionChanged += (_, _) => OnPropertyChanged(nameof(GitOutputLogText));
    }

    /// <summary>Whatever IsAiWorking was the moment a hidden turn started (see OnGenerateHiddenTurnStarted/Finished) - null while no hidden turn is in flight. Restoring to this, rather than blindly clearing IsAiWorking, keeps the workspace locked afterward when the hidden turn was nested inside an already-locked flow.</summary>
    private bool? _wasAiWorkingBeforeHiddenTurn;

    [ObservableProperty]
    private GitTarget? _target;

    [ObservableProperty]
    private bool _hasPendingChanges;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True once the current busy action has failed (see MarkFailed) - the overlay swaps its Cancel button for Confirm and stops auto-closing once the action itself finishes, so the user always gets a chance to actually read GitOutputLog before it disappears. Reset at the start of every RunBusyAsync call.</summary>
    [ObservableProperty]
    private bool _isBusyFailed;

    /// <summary>True from the moment a user submits a Generate message until the turn finishes - see OnGenerateNormalTurnStarted/Completed. Drives IsInteractionBlocked, which locks the sidebar sections and (via WorkspaceViewModel/WorkspaceContentViewModel) the Edit tab and History tab's controls.</summary>
    [ObservableProperty]
    private bool _isAiWorking;

    /// <summary>Set by WorkspaceViewModel from FilesSectionViewModel.HasRunningScripts - true while any .cs file in this workspace is running. Folded into IsInteractionBlocked so a running script locks Commit/Merge/etc. here and every History tab action exactly like a busy version action or an in-flight AI turn already does: manual editing, script running, and AI working are meant to be mutually exclusive states over the same working tree.</summary>
    [ObservableProperty]
    private bool _hasRunningScripts;

    /// <summary>True while the active Generate turn is paused (GenerateTabViewModel.TurnPaused/TurnResumed) - IsAiWorking stays true the whole time too (see OnGenerateNormalTurnStarted/Completed, deliberately not fired around a pause), so the workspace stays exactly as locked as it was while genuinely working; this only distinguishes the bottom status bar's own "AI is paused" text from "AI work in progress…" (see MainShellView.axaml).</summary>
    [ObservableProperty]
    private bool _isAiPaused;

    /// <summary>The current busy action's own live git command log (command lines plus their output) - see RunBusyAsync/GitCommandLogSink. Shown in the busy overlay; cleared at the start of every new action.</summary>
    public ObservableCollection<string> GitOutputLog { get; } = [];

    /// <summary>GitOutputLog joined into one string, newest content last - what the busy overlay's own SelectableTextBlock actually binds to (see WorkspaceView.axaml), so the whole log selects/copies as one continuous block instead of needing to be dragged across one unselectable TextBlock per line.</summary>
    public string GitOutputLogText => string.Join('\n', GitOutputLog);

    /// <summary>Blocks every git action triggered from the History tab or this section's own Commit/Reset - true during a git-only action (IsBusy), the whole Generate-turn-plus-commit workflow (IsAiWorking), or a running .cs file (HasRunningScripts).</summary>
    public bool IsInteractionBlocked => IsBusy || IsAiWorking || HasRunningScripts;

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

    partial void OnIsBusyFailedChanged(bool value)
    {
        CancelBusyCommand.NotifyCanExecuteChanged();
        ConfirmBusyCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsAiWorkingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInteractionBlocked));
        NotifyMutatingCommandsCanExecuteChanged();
    }

    partial void OnHasRunningScriptsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsInteractionBlocked));
        NotifyMutatingCommandsCanExecuteChanged();
    }

    /// <summary>Raised whenever the targeted branch/tag/commit changes, at the end of every RefreshAsync.</summary>
    public event Action<GitTarget?>? TargetChanged;

    /// <summary>Raised the instant ResolveConflictsAsync actually starts working a conflict (never for a call that turns out to be a no-op) - WorkspaceViewModel switches WorkspaceContentViewModel.SelectedTabIndex to Generate in response, so the user lands on the exchange automatically instead of needing to notice IsAiWorking flipped on and go find it themselves.</summary>
    public event Action? SwitchToGenerateRequested;

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

            // "Not initialized yet" covers both a literally empty folder AND a repo freshly cloned from an
            // empty remote (see IsRepoInitializedAsync's own doc comment) - exactly the two cases scaffolding
            // from a template makes sense for. Skipped if InitializeRepoAsync itself failed.
            if (!IsBusyFailed)
            {
                await OfferScaffoldFromTemplateAsync();
            }
        }
        else
        {
            await RefreshAsync();

            // Catches the local-exclude pattern for a repo that predates it, or a newer AutoDev build adding
            // to it - a no-op (pure .git/info/exclude bookkeeping, outside the working tree) if already present.
            await versioningService.EnsureLocalGitExcludeAsync();
        }

        // Guarded rather than an unconditional Start() - the tab could have already been switched away from
        // (see SetActive) by the time this async setup finishes, in which case the periodic sync shouldn't
        // start running at all until it's actually selected again.
        if (isActive)
        {
            periodicSyncTimer.Start();
        }
    }

    /// <summary>
    /// Pauses (Deactivate) or resumes (Activate) the periodic background remote sync while this workspace's
    /// own tab isn't the one currently selected - see FilesSectionViewModel.SetActive's identical reasoning.
    /// An in-flight busy action (RunBusyAsync) or AI turn is never interrupted by this - it's a plain
    /// already-running Task either way, wholly unrelated to periodicSyncTimer, so it keeps running to
    /// completion regardless of which tab is selected.
    /// </summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            Activate();
        }
        else
        {
            Deactivate();
        }
    }

    private void Activate()
    {
        if (isActive)
        {
            return;
        }

        isActive = true;
        periodicSyncTimer.Start();

        // Nothing re-synced while paused - a task run, or an action from elsewhere (another clone, a git
        // command run outside this app), could have changed the target/pending-changes state in the
        // meantime. Skipped while a busy action or AI turn is already in flight - whatever's running will
        // settle this itself once it finishes, and reading git state concurrently with it risks a confusing
        // intermediate read.
        if (!IsInteractionBlocked)
        {
            _ = RefreshAsync();
        }
    }

    private void Deactivate()
    {
        if (!isActive)
        {
            return;
        }

        isActive = false;
        periodicSyncTimer.Stop();
    }

    /// <summary>
    /// Set by WorkspaceViewModel to flush the Edit tab's pending debounced autosave before every mutating
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
    /// racing to be first past a check. The overlay only auto-closes once action returns without ever calling
    /// MarkFailed - if it did, the overlay stays up (Confirm replaces Cancel) until the user dismisses it
    /// themselves, so the git log a failure happened alongside is never yanked away before they can read it.
    /// Checks for a configured git identity (user.name/user.email) before any of that - prompting once and
    /// configuring it globally, rather than letting the action itself fail with git's own "Please tell me who
    /// you are" the moment it turns out to need one (a commit, an annotated tag, a squash/rebase, ...); every
    /// call goes through this same check rather than only the specific actions that need it, since that list
    /// isn't worth maintaining when the check itself is one cheap `git config --get` away from certain either
    /// way.
    /// </summary>
    public async Task RunBusyAsync(Func<CancellationToken, Task> action)
    {
        if (!await versioningService.HasUserIdentityConfiguredAsync())
        {
            GitIdentityDialogResult? identity = await dialogService.ShowGitIdentityDialogAsync();
            if (identity is null)
            {
                return;
            }

            await versioningService.SetGlobalUserIdentityAsync(identity.Name, identity.Email);
        }

        IsBusy = true;
        IsBusyFailed = false;
        GitOutputLog.Clear();
        GitActionSnapshot snapshot = await versioningService.CaptureSnapshotAsync();
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
        catch (Exception ex)
        {
            // A normal git failure (bad credentials, no permission, a rejected push, ...) already comes back
            // as an ordinary false/GitOperationOutcome.Failed result, not an exception - see RunAsync's
            // GIT_TERMINAL_PROMPT/GIT_SSH_COMMAND overrides, which exist specifically so none of that ever
            // hangs or throws instead. This is the backstop for anything that still somehow does (git itself
            // vanishing mid-session, a truly unexpected process failure, ...), so it fails as visibly as a
            // normal action failure rather than crashing the whole app.
            await versioningService.RevertToSnapshotAsync(snapshot);
            MarkFailed($"Error: {ex.Message}");
        }
        finally
        {
            GitCommandLogSink.Current = null;
            busyCts.Dispose();
            busyCts = null;
            CancelBusyCommand.NotifyCanExecuteChanged();
        }

        if (IsBusyFailed)
        {
            busyConfirmTcs = new TaskCompletionSource();
            ConfirmBusyCommand.NotifyCanExecuteChanged();
            await busyConfirmTcs.Task;
            busyConfirmTcs = null;
        }

        IsBusy = false;
        await RefreshAsync();
    }

    /// <summary>Marks the current RunBusyAsync call as failed - appends `message` to GitOutputLog (rather than a separate popup) and keeps the overlay up, Confirm in place of Cancel, until the user dismisses it themselves. Called by an action's own lambda (this section's own, or HistoryTabViewModel's - see RunBusyAsync) in place of the old ShowMessageDialogAsync so the failure reason and the log it happened alongside are always reviewed together, never a popup that could be dismissed (and the overlay behind it auto-closed) without actually reading the log.</summary>
    public void MarkFailed(string message)
    {
        IsBusyFailed = true;
        GitOutputLog.Add(message);
    }

    private bool CanCancelBusy() => busyCts is not null && !IsBusyFailed;

    /// <summary>The busy overlay's own Cancel button - signals the running action's CancellationToken, which RunBusyAsync's catch block turns into a revert back to the pre-action snapshot. Hidden (see CanCancelBusy/WorkspaceView.axaml) once the action has already finished and failed - ConfirmBusy takes over from there.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelBusy))]
    private void CancelBusy() => busyCts?.Cancel();

    private bool CanConfirmBusy() => IsBusyFailed;

    /// <summary>The busy overlay's own Confirm button, shown only once the current action has failed (see MarkFailed) - lets RunBusyAsync finally close the overlay instead of it auto-closing the moment the action itself returns.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmBusy))]
    private void ConfirmBusy() => busyConfirmTcs?.TrySetResult();

    /// <summary>
    /// Shared conflict-resolution loop for this section's own Rebase/Merge, HistoryTabViewModel's Merge Into
    /// Current/Rebase Current Onto This, and PullWithStashIfNeededAsync's own stash-pop conflicts - a no-op
    /// unless the initial attempt already came back Conflicts. Locks the sidebar/Edit/History controls via
    /// IsAiWorking for the whole loop, exactly like a normal Generate turn, since Claude is actively editing
    /// files here just the same - only IsBusy (the busy overlay) drops out during the actual
    /// RunAutomatedTurnAsync call, so the user can watch the exchange happen in Generate (switched to
    /// automatically - see SwitchToGenerateRequested). Restores IsAiWorking to whatever it was before (rather
    /// than unconditionally clearing it) since this can run nested inside an already-locked flow.
    /// continueAction is ContinueRebaseAsync/ContinueMergeAsync for those two callers; for a stash-pop conflict
    /// there's no git "continue" step (resolving and staging the files IS the fix), so that caller passes a
    /// lambda that just re-confirms success. buildInstruction defaults to the generic rebase/merge wording;
    /// PullWithStashIfNeededAsync passes its own, since "this produced merge conflicts" doesn't fit a stash
    /// pop and the AI also needs to know it's reconciling stashed local changes against newly-pulled commits,
    /// not two branches.
    /// </summary>
    public async Task<GitOperationOutcome> ResolveConflictsAsync(
        GitOperationOutcome outcome,
        Func<CancellationToken, Task<GitOperationOutcome>> continueAction,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<string>, string>? buildInstruction = null)
    {
        if (outcome != GitOperationOutcome.Conflicts)
        {
            return outcome;
        }

        buildInstruction ??= BuildConflictInstruction;

        bool wasAlreadyLocked = IsAiWorking;

        // Restored (not just unconditionally set true) once the whole loop ends, below - this section's own
        // Rebase/Merge and HistoryTabViewModel's Merge Into Current/Rebase Current Onto This always call this
        // from inside RunBusyAsync, where IsBusy is already true; restoring it back to true afterward keeps
        // the overlay up for whatever git work they still have left (ContinueMergeAsync/push), until
        // RunBusyAsync's own tail finally drops it. PullWithStashIfNeededAsync calls this with no such
        // enclosing RunBusyAsync at all (IsBusy starts false) - restoring to false instead here is what
        // actually lets its overlay-free flow stay overlay-free once conflict resolution finishes, rather
        // than getting stuck showing "Working…" forever with nothing left to ever clear it.
        bool wasBusyBeforeLoop = IsBusy;

        IsAiWorking = true;
        SwitchToGenerateRequested?.Invoke();
        try
        {
            for (int attempt = 0; outcome == GitOperationOutcome.Conflicts && attempt < MaxConflictResolutionAttempts; attempt++)
            {
                IReadOnlyList<string> conflictedFiles = await versioningService.GetConflictedFilesAsync(cancellationToken);
                string instruction = buildInstruction(conflictedFiles);

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
            IsBusy = wasBusyBeforeLoop;
        }

        return outcome;
    }

    /// <summary>Asks whether to scaffold a just-initialized (empty) workspace from a template - see EnsureRepoAsync. A plain no-op if declined, or if the Templates popup is closed without applying one.</summary>
    private async Task OfferScaffoldFromTemplateAsync()
    {
        if (!await dialogService.ShowConfirmDialogAsync(
                "Scaffold Workspace",
                "This workspace is empty. Would you like to scaffold it from a template?",
                confirmLabel: "Choose Template",
                isDestructive: false))
        {
            return;
        }

        if (await dialogService.ShowTemplatesDialogAsync(canApply: true) is { } applied)
        {
            await ApplyTemplateAsync(applied.Name, applied.Content);
        }
    }

    /// <summary>
    /// Submits a real Generate request (see GenerateTabViewModel.SubmitRequestAsync) instructing the AI to
    /// apply a template's content (see ITemplateService/TemplatesDialogViewModel) to this workspace, with
    /// "Apply template {templateName}" as the request card's own display text rather than the template's full
    /// raw content - switches to Generate automatically so the user lands on it right away, same as a
    /// genuinely typed message would once submitted. IsAiWorking is handled by the usual
    /// OnGenerateNormalTurnStarted/Completed subscription below, exactly like any other Generate request - no
    /// separate handling needed here. Works the same whether the workspace is empty (scaffolding it fresh) or
    /// already has content (restructuring it to conform, up to and including a large rewrite - see
    /// BuildTemplateInstruction) - the instruction itself covers both, so this doesn't need to distinguish
    /// them. Called either right after EnsureRepoAsync silently initializes a brand-new empty repo (offering
    /// to scaffold it - see EnsureRepoAsync), or from the title bar's Templates popup applying one to whatever
    /// workspace happens to be open at the time.
    /// </summary>
    public async Task ApplyTemplateAsync(string templateName, string templateContent)
    {
        if (IsInteractionBlocked)
        {
            await dialogService.ShowMessageDialogAsync("Apply Template", "Can't apply a template while the workspace is busy - try again once the current action finishes.");
            return;
        }

        SwitchToGenerateRequested?.Invoke();
        await generate.SubmitRequestAsync($"Apply template {templateName}", BuildTemplateInstruction(templateContent));
    }

    private static string BuildTemplateInstruction(string templateContent) =>
        "Apply the following template to this workspace, which describes how it should be organized and " +
        "configured. This applies equally whether the workspace is currently empty or already has content: " +
        "if empty, scaffold the initial structure the template describes; if it already has files, restructure " +
        "and rewrite whatever's there as needed so the workspace conforms to the template, moving/renaming/" +
        "deleting/rewriting existing files as needed rather than only adding alongside them.\n\n" +
        "The template may itself be written as a diff against some other baseline (e.g. \"inherits from " +
        "X, only what's different is listed below\") that this workspace was never literally built from - " +
        "don't treat that as a blocker or a reason to stop and ask. Read through the template for its actual " +
        "intent (the conventions, layout, and configuration it's steering toward), compare that against " +
        "this workspace's current structure/naming/conventions, and map the existing workspace onto that " +
        "intent yourself: e.g. if the template says some directory replaces another with the same internal " +
        "shape, find this workspace's own equivalent (even if named or organized differently) and restructure " +
        "it accordingly, adapting file/config content along the way rather than only renaming folders.\n\n" +
        "A large rewrite of the existing workspace is expected and fully intended here, not a concern to " +
        "flag or hesitate over on its own - this workspace is under version control, so every change here " +
        "is fully reversible. For an empty workspace, or an existing one where the template's intent maps " +
        "onto it reasonably cleanly, proceed directly without asking first.\n\n" +
        "If the workspace already has content, though, and applying the template would mean a major concern " +
        "beyond the restructuring's sheer size - e.g. the template describes a fundamentally different kind " +
        "of project than what's actually here, or following it faithfully would mean discarding substantial " +
        "existing functionality that has nothing to do with what the template covers - stop and ask first, " +
        "explaining the concern, rather than proceeding on your own judgment call. This is a normal " +
        "conversation the user can see and reply to, so a real question here gets a real answer.\n\n" +
        templateContent;

    private static string BuildConflictInstruction(IReadOnlyList<string> conflictedFiles) =>
        "This produced merge conflicts in: " + string.Join(", ", conflictedFiles) + ". " +
        "Open each file, resolve the conflict by editing it to the correct final content and removing the " +
        "conflict markers (<<<<<<<, =======, >>>>>>>), then stage the resolved files with `git add`. Reply once " +
        "every conflict is resolved and staged.";

    /// <summary>
    /// After a fetch (see RefreshAsync/PeriodicSync, both of which run this via HistoryTabViewModel.
    /// RefreshFromRemoteAsync), transparently pulls the current branch if the remote moved ahead of it - even
    /// with pending changes in the way, unlike the plain PullCurrentBranchAsync path this replaces: pending
    /// changes (tracked and untracked alike) are stashed first, then popped back on top once the pull lands.
    /// A no-op (PullWithStashOutcome.NothingToDo) if there's nothing new to pull. If popping the stash
    /// conflicts with what was just pulled in, resolves it exactly like a Rebase/Merge conflict (see
    /// ResolveConflictsAsync) - switching to Generate automatically - with an instruction that explains the
    /// situation is a stashed-changes-vs-newly-pulled-commits reconciliation, not two branches; the stash is
    /// dropped only once actually resolved (a clean pop already drops its own). Left conflicted and un-dropped
    /// if resolution still fails after every attempt, for the user to sort out by hand.
    /// </summary>
    public async Task PullWithStashIfNeededAsync(CancellationToken cancellationToken = default)
    {
        PullWithStashResult result = await versioningService.PullCurrentBranchWithStashAsync(cancellationToken);
        if (result.Outcome != PullWithStashOutcome.Conflicts)
        {
            return;
        }

        GitOperationOutcome resolved = await ResolveConflictsAsync(
            GitOperationOutcome.Conflicts,
            _ => Task.FromResult(GitOperationOutcome.Succeeded),
            cancellationToken,
            conflictedFiles => BuildStashConflictInstruction(conflictedFiles, result.OriginalCommitHash));

        if (resolved != GitOperationOutcome.Conflicts)
        {
            await versioningService.DropStashAsync(cancellationToken);
        }
    }

    private static string BuildStashConflictInstruction(IReadOnlyList<string> conflictedFiles, string? originalCommitHash) =>
        "New commits were just pulled into the current branch, and reapplying your stashed pending changes " +
        "on top produced merge conflicts in: " + string.Join(", ", conflictedFiles) + ". " +
        (originalCommitHash is not null
            ? $"The current branch was at commit {originalCommitHash} before this pull - everything reachable " +
              "from HEAD since then is a newly-pulled commit, not part of the stashed changes. "
            : "") +
        "Open each file, resolve the conflict by editing it to the correct final content - respecting both the " +
        "intent of the stashed (previously pending, uncommitted) changes and everything the newly-pulled " +
        "commits changed - and removing the conflict markers (<<<<<<<, =======, >>>>>>>), then stage the " +
        "resolved files with `git add`. Reply once every conflict is resolved and staged.";

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
        string? message = await dialogService.ShowInputDialogAsync("Commit", "Message");
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
        string? name = await dialogService.ShowInputDialogAsync("Branch", "Branch name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string trimmedName = name.Trim();
        await RunBusyAsync(async ct =>
        {
            BranchCreationOutcome outcome = await versioningService.CreateBranchAsync(trimmedName, "HEAD", ct);
            if (outcome == BranchCreationOutcome.IdAlreadyExists)
            {
                MarkFailed($"A branch named \"{trimmedName}\" already exists.");
            }
        });
    }

    /// <summary>Creates an annotated tag (always - never a plain lightweight one, and always with a blank message) at the current target.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task TagAsync()
    {
        string? name = await dialogService.ShowInputDialogAsync("Tag", "Tag name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string trimmedName = name.Trim();
        await RunBusyAsync(async ct =>
        {
            TagCreationOutcome outcome = await versioningService.CreateTagAsync(trimmedName, "HEAD", ct);
            if (outcome == TagCreationOutcome.IdAlreadyExists)
            {
                MarkFailed($"A tag named \"{trimmedName}\" already exists.");
            }
        });
    }

    /// <summary>Configures or repoints the "origin" remote.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task RemoteAsync()
    {
        string currentUrl = await versioningService.GetRemoteUrlAsync() ?? "";
        string? newUrl = await dialogService.ShowInputDialogAsync("Remote", "Remote URL", currentUrl);
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
        IReadOnlyList<string> branches = await versioningService.GetEligibleBaseBranchesAsync();
        if (branches.Count == 0)
        {
            await dialogService.ShowMessageDialogAsync("Squash", "No other branch to squash against.");
            return;
        }

        SquashDialogResult? result = await dialogService.ShowSquashDialogAsync(branches, branch => versioningService.GetDefaultSquashMessageAsync(branch));
        if (result is null)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            if (!await versioningService.SquashAsync(result.BaseBranch, result.Message.Trim(), ct))
            {
                MarkFailed("Squash succeeded locally, but pushing it to the remote failed.");
            }
        });
    }

    /// <summary>Rebases the current branch onto a chosen branch, always squashing its own commits first - only offered while targeting a branch, see VersionSectionView. Merge conflicts, if any, are handed to Claude via ResolveConflictsAsync.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task RebaseAsync()
    {
        IReadOnlyList<string> branches = await versioningService.GetEligibleBaseBranchesAsync();
        if (branches.Count == 0)
        {
            await dialogService.ShowMessageDialogAsync("Rebase", "No other branch to rebase onto.");
            return;
        }

        RebaseDialogResult? result = await dialogService.ShowRebaseDialogAsync(branches, branch => versioningService.GetDefaultSquashMessageAsync(branch));
        if (result is null)
        {
            return;
        }

        await RunBusyAsync(async ct =>
        {
            GitOperationOutcome outcome = await versioningService.RebaseWithSquashAsync(result.OntoBranch, result.SquashMessage.Trim(), ct);
            outcome = await ResolveConflictsAsync(outcome, ct2 => versioningService.ContinueRebaseAsync(ct2), ct);
            if (outcome == GitOperationOutcome.Succeeded)
            {
                if (!await versioningService.PushCurrentBranchAsync(force: true, ct))
                {
                    MarkFailed("Rebase succeeded locally, but pushing it to the remote failed.");
                }
            }
            else if (outcome == GitOperationOutcome.Conflicts)
            {
                await versioningService.AbortRebaseAsync(ct);
                MarkFailed("Could not automatically resolve the rebase conflicts - aborted.");
            }
            else
            {
                MarkFailed("Rebase failed.");
            }
        });
    }

    /// <summary>Fast-forward merges the current branch onto a chosen target branch, squashing first if there's more than one commit to bring over - only offered while targeting a branch, see VersionSectionView. Never conflicts (a fast-forward can't) - fails outright if the current branch isn't actually based on the target's own head. On success, the now-merged original branch is deleted both locally and on the remote (see IWorkspaceVersioningService.FastForwardMergeAsync, which leaves targetBranch - not the original branch - checked out afterward specifically so this can actually delete it).</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task MergeAsync()
    {
        IReadOnlyList<string> branches = await versioningService.GetEligibleMergeTargetBranchesAsync();
        if (branches.Count == 0)
        {
            await dialogService.ShowMessageDialogAsync("Merge", "No branch this branch can be fast-forward merged onto.");
            return;
        }

        MergeDialogResult? result = await dialogService.ShowMergeDialogAsync(branches, branch => versioningService.GetDefaultSquashMessageAsync(branch));
        if (result is null)
        {
            return;
        }

        string? originalBranch = Target?.BranchName;
        await RunBusyAsync(async ct =>
        {
            bool succeeded = await versioningService.FastForwardMergeAsync(result.TargetBranch, result.SquashMessage?.Trim(), ct);
            if (!succeeded)
            {
                MarkFailed($"'{originalBranch}' isn't based on the head of '{result.TargetBranch}' - can't fast-forward.");
                return;
            }

            if (originalBranch is not null && !await versioningService.DeleteBranchEverywhereAsync(originalBranch, ct))
            {
                MarkFailed($"Merged, but deleting '{originalBranch}' on the remote failed.");
            }
        });
    }

    public void Dispose()
    {
        periodicSyncTimer.Stop();
        periodicSyncTimer.Dispose();
    }
}
