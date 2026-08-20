using System.Collections.ObjectModel;
using AutoDev.Core.Models;
using AutoDev.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoDev.ViewModels.Content;

/// <summary>
/// One row in the History tab's timeline - wraps the immutable BranchTimelineEntry with this row's own
/// expand/collapse UI state. A left-click expands/collapses in place to show the commit's changed files (see
/// HistoryTabViewModel.ToggleExpandedCommand) - Changes stays empty and unpopulated until that happens.
/// Right-clicking offers Checkout (plus Delete Tag for a Tag row) - see HistoryTabView.axaml's ContextMenu.
/// </summary>
public sealed partial class TimelineEntryViewModel(BranchTimelineEntry entry) : ViewModelBase
{
    public BranchTimelineEntry Entry { get; } = entry;

    /// <summary>True for the topmost row of the current page - see HistoryTabView's connecting Line, which is split top/bottom around each row's own glyph so the first row's top half (and the last row's bottom half, via IsLastInPage) doesn't draw a dangling stub with nothing above/below it to connect to.</summary>
    public bool IsFirstInPage { get; init; }

    /// <summary>True for the bottommost row of the current page - see IsFirstInPage.</summary>
    public bool IsLastInPage { get; init; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoChanges))]
    private bool _isLoadingChanges;

    public ObservableCollection<ChangeTreeNode> Changes { get; } = [];

    /// <summary>The context menu's Checkout item only makes sense for a row that isn't already what's checked out - switching to the thing you're already on would be a no-op.</summary>
    public bool CanCheckout => !Entry.IsCurrentCommit;

    /// <summary>Drives which of the two mutually-exclusive Grid.Column="2" elements in HistoryTabView shows - a tag's own badge-styled node, or the plain clickable row every other kind uses.</summary>
    public bool IsTag => Entry.Kind == BranchTimelineEntryKind.Tag;

    /// <summary>Only meaningful once IsLoadingChanges goes false - relies on HistoryTabViewModel.ToggleExpandedCommand always finishing populating Changes before flipping IsLoadingChanges off, so this is never (mis)read as "empty" while a fetch is still in flight.</summary>
    public bool HasNoChanges => !IsLoadingChanges && Changes.Count == 0;
}
