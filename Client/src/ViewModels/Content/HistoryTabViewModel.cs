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
/// A read-only branch/timeline browser: every local branch as a fully-expanded hierarchy tree (current pinned
/// first among roots), and the selected branch's own timeline, one page (100 entries) at a time - its base
/// commit up to its tip, with a parent-branch node before the base commit and a node for every branch created
/// from this one. Parent/child nodes only navigate this tab's own selection - no git action. A Commit row
/// expands in place to show that commit's changed files, with its own "switch to this commit" button for the
/// git action a plain click used to perform directly.
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
            }
        };
    }

    /// <summary>Disables every row in this tab while true - mirrors VersionSectionViewModel.IsInteractionBlocked, since a checkout here runs through _version.RunBusyAsync just like any Version-section action.</summary>
    public bool IsInteractionBlocked => _version.IsInteractionBlocked;

    private readonly List<BranchSummary> _branches = [];

    /// <summary>Root-level nodes of the fully-expanded branch hierarchy tree (see BranchTreeNodeViewModel) - rebuilt from the flat branch list every time it changes.</summary>
    public ObservableCollection<BranchTreeNodeViewModel> BranchTree { get; } = [];

    public ObservableCollection<TimelineEntryViewModel> TimelineEntries { get; } = [];

    [ObservableProperty]
    private string? _selectedBranchId;

    [ObservableProperty]
    private string _selectedBranchName = "";

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

    partial void OnSelectedBranchIdChanged(string? value)
    {
        UpdateTreeSelection();
        _ = LoadTimelineAsync(value, 0);
    }

    /// <summary>
    /// LoadBranchesAsync/LoadTimelineAsync can each be triggered from multiple independent sources at once
    /// (tab activation, _version.TargetChanged after every action AND every periodic background sync tick,
    /// a user click reassigning SelectedBranchId) - without guarding against overlap, two concurrent calls
    /// each doing their own Clear()-then-Add() could interleave and leave the collection with duplicated
    /// rows. Each call captures its own token and only applies its results if no newer call has started
    /// since - a stale in-flight call's results are simply discarded rather than applied out of order.
    /// </summary>
    private int _branchesLoadToken;
    private int _timelineLoadToken;

    /// <summary>Called each time the History tab is activated, and whenever the targeted branch changes elsewhere in the app, so it reflects the latest branch list/lineage.</summary>
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
        RebuildBranchTree();

        var stillPresent = branches.Select(b => b.Id).ToHashSet();
        if (SelectedBranchId is null || !stillPresent.Contains(SelectedBranchId))
        {
            SelectedBranchId = branches.FirstOrDefault()?.Id; // current branch sorts first - see ListAllBranchesAsync
        }
        else
        {
            UpdateTreeSelection();
            await LoadTimelineAsync(SelectedBranchId, TimelinePageIndex);
        }
    }

    /// <summary>Rebuilds the whole tree from _branches - the branch list is small and changes rarely enough that a full rebuild (rather than the Files tree's incremental-diff approach) is simplest, at no real cost.</summary>
    private void RebuildBranchTree()
    {
        BranchTree.Clear();
        var byParent = _branches.ToLookup(b => b.ParentId);
        var known = _branches.Select(b => b.Id).ToHashSet();

        BranchTreeNodeViewModel Build(BranchSummary branch)
        {
            var node = new BranchTreeNodeViewModel(branch) { IsSelected = branch.Id == SelectedBranchId };
            foreach (var child in byParent[branch.Id].OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                node.Children.Add(Build(child));
            }

            return node;
        }

        // A root is any branch with no ParentId, or whose declared parent no longer exists locally (e.g. the
        // parent branch was deleted after merging) - either way it has nowhere else to nest under.
        var roots = _branches.Where(b => b.ParentId is null || !known.Contains(b.ParentId));
        foreach (var root in roots.OrderByDescending(b => b.IsCurrent).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase))
        {
            BranchTree.Add(Build(root));
        }
    }

    private void UpdateTreeSelection()
    {
        void Walk(IEnumerable<BranchTreeNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                node.IsSelected = node.Branch.Id == SelectedBranchId;
                Walk(node.Children);
            }
        }

        Walk(BranchTree);
    }

    private async Task LoadTimelineAsync(string? branchId, int pageIndex)
    {
        var token = ++_timelineLoadToken;
        var page = branchId is null ? null : await _versioningService.GetBranchTimelinePageAsync(branchId, pageIndex, PageSize);
        if (token != _timelineLoadToken)
        {
            return; // a newer LoadTimelineAsync call started while this one was awaiting - let it win
        }

        TimelineEntries.Clear(); // also drops any expanded row's populated Changes - nothing stays loaded once its branch/page is left
        SelectedBranchName = page?.BranchName ?? branchId ?? "";
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
    private Task PreviousTimelinePage() => LoadTimelineAsync(SelectedBranchId, TimelinePageIndex - 1);

    private bool CanGoToNextTimelinePage() => TimelinePageIndex < TimelinePageCount - 1;

    [RelayCommand(CanExecute = nameof(CanGoToNextTimelinePage))]
    private Task NextTimelinePage() => LoadTimelineAsync(SelectedBranchId, TimelinePageIndex + 1);

    /// <summary>A parent/child node's click - pure view navigation to that branch's own timeline, no git action involved.</summary>
    [RelayCommand]
    private void NavigateTo(string branchId) => SelectedBranchId = branchId;

    /// <summary>A Commit (or Tag - see BranchTimelineEntryKind.Tag) row's click - expands/collapses its commit's changes tree in place. Populated lazily on expand, and dropped again on collapse (see TimelineEntryViewModel).</summary>
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

    /// <summary>The expanded view's "switch to this commit" button - detaches HEAD at that exact commit, confirming discard of pending changes first (same warning style as the old switch-confirmation flow).</summary>
    [RelayCommand]
    private async Task CheckoutCommitAsync(string commitHash)
    {
        var hasPendingChanges = await _versioningService.HasUncommittedChangesAsync();
        var message = hasPendingChanges
            ? "Check out this commit? This will discard your pending changes."
            : "Check out this commit? This detaches HEAD at a read-only historical snapshot.";
        if (!await _dialogService.ShowConfirmDialogAsync("Checkout Commit", message, confirmLabel: hasPendingChanges ? "Discard and Checkout" : "Checkout", isDestructive: hasPendingChanges))
        {
            return;
        }

        await _version.RunBusyAsync(async () =>
        {
            // Checkout fails (silently stays put) with a dirty working tree - discard first, exactly like the
            // confirmation just above already told the user would happen (see the old DiscardPendingChangesIfAnyAsync
            // this carries forward the reasoning from).
            if (hasPendingChanges)
            {
                await _versioningService.ResetAsync();
            }

            await _versioningService.CheckoutRefAsync(commitHash);
        });
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
}
