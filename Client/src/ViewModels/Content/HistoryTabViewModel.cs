using System.Collections.ObjectModel;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels;
using AutoDev.ViewModels.Infrastructure;
using AutoDev.ViewModels.Sidebar;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Content;

/// <summary>
/// A read-only branch/timeline browser, plus every git action that targets some other row instead of the
/// currently checked-out branch itself (Commit/Reset/Branch/Tag/Remote/Squash/Rebase all live on the Version
/// section instead - see VersionSectionViewModel) as a right-click context menu on a branch/commit/tag row -
/// see BranchRows/TimelineEntries and the Checkout/MergeIntoCurrent/RebaseCurrentOnto/Delete/DeleteTag commands
/// below. Every local branch is a flat row (current pinned first - see
/// IWorkspaceVersioningService.ListAllBranchesAsync); the selected branch's own commit/tag history shows one
/// page (100 entries) at a time, newest first. A Commit or Tag row's left-click expands in place to show that
/// commit's changed files (right-click only opens its context menu - see HistoryTabView.OnEntryPointerPressed).
/// </summary>
public sealed partial class HistoryTabViewModel : ViewModelBase
{
    private const int PageSize = 100;

    private readonly IWorkspaceVersioningService _versioningService;
    private readonly VersionSectionViewModel _version;
    private readonly IDialogService _dialogService;
    private readonly EditTabViewModel _edit;

    public HistoryTabViewModel(IWorkspaceVersioningService versioningService, VersionSectionViewModel version, IDialogService dialogService, EditTabViewModel edit)
    {
        _versioningService = versioningService;
        _version = version;
        _dialogService = dialogService;
        _edit = edit;
        _version.TargetChanged += target => { _ = LoadBranchesAsync(); };
        _version.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VersionSectionViewModel.IsInteractionBlocked))
            {
                OnPropertyChanged(nameof(IsInteractionBlocked));
                NotifyMutatingCommandsCanExecuteChanged();
            }
        };
    }

    /// <summary>Disables every mutating action in this tab while true - mirrors VersionSectionViewModel.IsInteractionBlocked, since every action here runs through _version.RunBusyAsync just like the old Version section's own buttons did.</summary>
    public bool IsInteractionBlocked => _version.IsInteractionBlocked;

    /// <summary>Shared CanExecute for every action command below (Checkout/MergeIntoCurrent/RebaseCurrentOnto/Delete/DeleteTag) - browsing the timeline itself (paging, expanding a commit's changes, selecting a branch) is pure local view state and stays interactive regardless, matching the old Version-section-vs-timeline split.</summary>
    private bool CanMutate() => !IsInteractionBlocked;

    private void NotifyMutatingCommandsCanExecuteChanged()
    {
        FetchCommand.NotifyCanExecuteChanged();
        CheckoutCommand.NotifyCanExecuteChanged();
        MergeIntoCurrentCommand.NotifyCanExecuteChanged();
        RebaseCurrentOntoCommand.NotifyCanExecuteChanged();
        DeleteBranchCommand.NotifyCanExecuteChanged();
        DeleteTagCommand.NotifyCanExecuteChanged();
    }

    private readonly List<BranchSummary> _branches = [];

    /// <summary>The flat local-branch list (current branch first, then alphabetical - see IWorkspaceVersioningService.ListAllBranchesAsync) - rebuilt from scratch every time it changes, since the list is small and changes rarely enough that a full rebuild is simplest, at no real cost.</summary>
    public ObservableCollection<BranchRowViewModel> BranchRows { get; } = [];

    public ObservableCollection<TimelineEntryViewModel> TimelineEntries { get; } = [];

    [ObservableProperty]
    private string? _selectedBranchName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousTimelinePageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextTimelinePageCommand))]
    [NotifyPropertyChangedFor(nameof(TimelinePageLabel))]
    private int _timelinePageIndex;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousTimelinePageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextTimelinePageCommand))]
    [NotifyPropertyChangedFor(nameof(TimelinePageLabel))]
    private int _timelinePageCount = 1;

    public string TimelinePageLabel => $"Page {TimelinePageIndex + 1} of {TimelinePageCount}";

    partial void OnSelectedBranchNameChanged(string? value)
    {
        UpdateRowSelection();
        _ = LoadTimelineAsync(value, 0);
    }

    /// <summary>
    /// LoadBranchesAsync/LoadTimelineAsync can each be triggered from multiple independent sources at once
    /// (tab activation, _version.TargetChanged after every action AND every periodic background sync tick,
    /// a user click reassigning SelectedBranchName) - without guarding against overlap, two concurrent calls
    /// each doing their own Clear()-then-Add() could interleave and leave the collection with duplicated
    /// rows. Each call captures its own token and only applies its results if no newer call has started
    /// since - a stale in-flight call's results are simply discarded rather than applied out of order.
    /// </summary>
    private int _branchesLoadToken;
    private int _timelineLoadToken;

    /// <summary>
    /// Called automatically every time the History tab becomes the active one (see
    /// WorkspaceContentViewModel.OnSelectedTabIndexChanged) - fetches with prune (_version.RefreshAsync,
    /// the same fetch-prune-and-resync-non-current-branches the periodic background sync already runs, so
    /// this is really just "run that right now instead of waiting up to 60s") then, if that turned up new
    /// commits on the current branch's remote counterpart, transparently pulls them in - stashing pending
    /// changes first (and popping them back after) if there are any, rather than only pulling while the
    /// working tree happens to be clean (see VersionSectionViewModel.PullWithStashIfNeededAsync, a no-op when
    /// there's nothing new to pull). Runs with no busy overlay for the fetch/pull itself - it's an automatic
    /// refresh triggered by switching tabs, not a user-initiated mutation, exactly like the periodic sync it's
    /// piggybacking on; a stash-pop conflict is the one case that visibly locks the workspace and switches to
    /// Generate, same as any other conflict resolution. RefreshAsync's own TargetChanged (fired
    /// unconditionally) is what actually reloads BranchRows/TimelineEntries below, via this class's
    /// constructor subscription - no separate LoadBranchesAsync call needed here.
    /// </summary>
    public async Task RefreshFromRemoteAsync()
    {
        await _version.RefreshAsync();
        await _version.PullWithStashIfNeededAsync();
        await _version.RefreshAsync();
    }

    private bool CanFetch() => !IsInteractionBlocked;

    /// <summary>The History tab's own manual "Fetch" button - fetch with prune, same as the automatic per-tab-open refresh above, but never also pulls even with a clean working tree; a deliberate click means "check what's new," not "also apply it." Goes through the normal busy overlay like every other action here - RunBusyAsync's own trailing RefreshAsync fetches again regardless (a cheap, harmless no-op re-fetch when nothing changed), traded for this action's own intent staying explicit here rather than relying on that as a side effect.</summary>
    [RelayCommand(CanExecute = nameof(CanFetch))]
    private Task FetchAsync() => _version.RunBusyAsync(ct => _versioningService.SyncWithRemoteAsync(ct));

    /// <summary>Called each time the History tab is activated, and whenever the targeted branch changes elsewhere in the app, so it reflects the latest branch list.</summary>
    public async Task LoadBranchesAsync()
    {
        var token = ++_branchesLoadToken;
        var branches = await _versioningService.ListAllBranchesAsync();
        if (token != _branchesLoadToken)
        {
            return; // a newer LoadBranchesAsync call started while this one was awaiting - let it win
        }

        _branches.Clear();
        _branches.AddRange(branches);
        RebuildBranchRows();

        var stillPresent = branches.Select(b => b.Name).ToHashSet();
        if (SelectedBranchName is null || !stillPresent.Contains(SelectedBranchName))
        {
            SelectedBranchName = branches.FirstOrDefault()?.Name; // current branch sorts first - see ListAllBranchesAsync
        }
        else
        {
            UpdateRowSelection();
            await LoadTimelineAsync(SelectedBranchName, TimelinePageIndex);
        }
    }

    private void RebuildBranchRows()
    {
        BranchRows.Clear();
        foreach (var branch in _branches)
        {
            BranchRows.Add(new BranchRowViewModel(branch) { IsSelected = branch.Name == SelectedBranchName });
        }
    }

    private void UpdateRowSelection()
    {
        foreach (var row in BranchRows)
        {
            row.IsSelected = row.Branch.Name == SelectedBranchName;
        }
    }

    private async Task LoadTimelineAsync(string? branchName, int pageIndex)
    {
        var token = ++_timelineLoadToken;
        var page = branchName is null ? null : await _versioningService.GetBranchTimelinePageAsync(branchName, pageIndex, PageSize);
        if (token != _timelineLoadToken)
        {
            return; // a newer LoadTimelineAsync call started while this one was awaiting - let it win
        }

        TimelineEntries.Clear(); // also drops any expanded row's populated Changes - nothing stays loaded once its branch/page is left
        TimelinePageIndex = page?.PageIndex ?? 0;
        TimelinePageCount = page?.PageCount ?? 1;
        if (page is null)
        {
            return;
        }

        for (var i = 0; i < page.Entries.Count; i++)
        {
            TimelineEntries.Add(new TimelineEntryViewModel(page.Entries[i])
            {
                IsFirstInPage = i == 0,
                IsLastInPage = i == page.Entries.Count - 1,
            });
        }
    }

    private bool CanGoToPreviousTimelinePage() => TimelinePageIndex > 0;

    [RelayCommand(CanExecute = nameof(CanGoToPreviousTimelinePage))]
    private Task PreviousTimelinePage() => LoadTimelineAsync(SelectedBranchName, TimelinePageIndex - 1);

    private bool CanGoToNextTimelinePage() => TimelinePageIndex < TimelinePageCount - 1;

    [RelayCommand(CanExecute = nameof(CanGoToNextTimelinePage))]
    private Task NextTimelinePage() => LoadTimelineAsync(SelectedBranchName, TimelinePageIndex + 1);

    /// <summary>A branch row's left-click - pure view navigation to that branch's own timeline, no git action involved.</summary>
    [RelayCommand]
    private void SelectBranch(string branchName) => SelectedBranchName = branchName;

    /// <summary>A Commit (or Tag - see BranchTimelineEntryKind.Tag) row's left-click - expands/collapses its commit's changes tree in place. Populated lazily on expand, and dropped again on collapse (see TimelineEntryViewModel).</summary>
    [RelayCommand]
    private async Task ToggleExpandedAsync(TimelineEntryViewModel entry)
    {
        if (entry.Entry.Kind is not (BranchTimelineEntryKind.Commit or BranchTimelineEntryKind.Tag) || entry.Entry.CommitHash is not { } hash)
        {
            return;
        }

        if (entry.IsExpanded)
        {
            entry.IsExpanded = false;
            entry.Changes.Clear();
            return;
        }

        // Only one node's changes view is ever expanded at a time - expanding a new one collapses whichever
        // other one was open, dropping its populated Changes the same way an explicit collapse-click would.
        foreach (var other in TimelineEntries.Where(t => t.IsExpanded))
        {
            other.IsExpanded = false;
            other.Changes.Clear();
        }

        entry.IsExpanded = true;
        entry.IsLoadingChanges = true;
        try
        {
            var changes = await _versioningService.GetCommitChangesAsync(hash);
            if (!entry.IsExpanded)
            {
                return; // collapsed again while this was in flight
            }

            foreach (var node in ChangeTreeNode.Build(changes, hash))
            {
                entry.Changes.Add(node);
            }
        }
        finally
        {
            entry.IsLoadingChanges = false;
        }
    }

    /// <summary>A specific file's row within an expanded commit's changes tree - opens that file's before/after content for that exact commit in the Edit tab's read-only Diff mode. A no-op for a folder row (RelativePath/CommitHash are only ever set on a leaf - see ChangeTreeNode.Build).</summary>
    [RelayCommand]
    private async Task OpenChangeAsync(ChangeTreeNode node)
    {
        if (node.RelativePath is not { } path || node.CommitHash is not { } hash)
        {
            return;
        }

        var diff = await _versioningService.GetFileDiffAsync(hash, path);
        await _edit.LoadDiffAsync(Path.GetFileName(path), diff);
        _edit.RequestFocus();
    }

    // --- Actions - see each row's ContextMenu in HistoryTabView.axaml for where these are actually offered. ---

    /// <summary>Checks out a branch by name, or detaches HEAD at a specific commit/tag - shared by every row kind's own "Checkout" menu item.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task CheckoutAsync(string refName)
    {
        var hasPendingChanges = await _versioningService.HasUncommittedChangesAsync();
        var message = hasPendingChanges
            ? $"Check out '{refName}'? This will discard your pending changes."
            : $"Check out '{refName}'?";
        if (!await _dialogService.ShowConfirmDialogAsync("Checkout", message, confirmLabel: hasPendingChanges ? "Discard and Checkout" : "Checkout", isDestructive: hasPendingChanges))
        {
            return;
        }

        await _version.RunBusyAsync(async ct =>
        {
            // Checkout fails (silently stays put) with a dirty working tree - discard first, exactly like the
            // confirmation just above already told the user would happen.
            if (hasPendingChanges)
            {
                await _versioningService.ResetAsync(ct);
            }

            await _versioningService.CheckoutRefAsync(refName, ct);
        });
    }

    /// <summary>Merges sourceBranch into whatever's currently checked out - offered on every non-current branch row. On success, sourceBranch (now fully absorbed into current) is deleted both locally and on the remote - unlike VersionSectionViewModel.MergeAsync's own fast-forward Merge, this never moves HEAD off the branch the user was already on, so sourceBranch is always safe to delete immediately.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task MergeIntoCurrentAsync(string sourceBranch)
    {
        await _version.RunBusyAsync(async ct =>
        {
            var outcome = await _versioningService.MergeAsync(sourceBranch, ct);
            outcome = await _version.ResolveConflictsAsync(outcome, ct2 => _versioningService.ContinueMergeAsync(ct2), ct);
            if (outcome == GitOperationOutcome.Succeeded)
            {
                if (!await _versioningService.PushCurrentBranchAsync(force: true, ct))
                {
                    _version.MarkFailed("Merge succeeded locally, but pushing it to the remote failed.");
                    return;
                }

                if (!await _versioningService.DeleteBranchEverywhereAsync(sourceBranch, ct))
                {
                    _version.MarkFailed($"Merged, but deleting '{sourceBranch}' on the remote failed.");
                }
            }
            else if (outcome == GitOperationOutcome.Conflicts)
            {
                await _versioningService.AbortMergeAsync(ct);
                _version.MarkFailed("Could not automatically resolve the merge conflicts - aborted.");
            }
            else
            {
                _version.MarkFailed("Merge failed.");
            }
        });
    }

    /// <summary>Rebases whatever's currently checked out onto ontoBranch - offered on every non-current branch row.</summary>
    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task RebaseCurrentOntoAsync(string ontoBranch)
    {
        await _version.RunBusyAsync(async ct =>
        {
            var outcome = await _versioningService.RebaseAsync(ontoBranch, ct);
            outcome = await _version.ResolveConflictsAsync(outcome, ct2 => _versioningService.ContinueRebaseAsync(ct2), ct);
            if (outcome == GitOperationOutcome.Succeeded)
            {
                if (!await _versioningService.PushCurrentBranchAsync(force: true, ct))
                {
                    _version.MarkFailed("Rebase succeeded locally, but pushing it to the remote failed.");
                }
            }
            else if (outcome == GitOperationOutcome.Conflicts)
            {
                await _versioningService.AbortRebaseAsync(ct);
                _version.MarkFailed("Could not automatically resolve the rebase conflicts - aborted.");
            }
            else
            {
                _version.MarkFailed("Rebase failed.");
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task DeleteBranchAsync(string name)
    {
        if (!await _dialogService.ShowConfirmDialogAsync("Delete Branch", $"Delete branch '{name}'? This cannot be undone.", confirmLabel: "Delete"))
        {
            return;
        }

        await _version.RunBusyAsync(ct => _versioningService.DeleteBranchAsync(name, ct));
    }

    [RelayCommand(CanExecute = nameof(CanMutate))]
    private async Task DeleteTagAsync(string name)
    {
        if (!await _dialogService.ShowConfirmDialogAsync("Delete Tag", $"Delete tag '{name}'? This cannot be undone.", confirmLabel: "Delete"))
        {
            return;
        }

        await _version.RunBusyAsync(ct => _versioningService.DeleteTagAsync(name, ct));
    }
}
