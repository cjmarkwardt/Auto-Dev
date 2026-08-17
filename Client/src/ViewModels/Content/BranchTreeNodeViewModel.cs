using System.Collections.ObjectModel;
using AutoDev.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoDev.ViewModels.Content;

/// <summary>One row in the History tab's branch hierarchy TreeView - a BranchSummary plus whichever other branches list it as their parent, recursively. Always shown fully expanded (see HistoryTabView's IsExpanded style), so there's no lazy-loading/placeholder concern here unlike FileTreeNodeViewModel - the whole (typically small) branch list is already in hand.</summary>
public sealed partial class BranchTreeNodeViewModel(BranchSummary branch) : ViewModelBase
{
    [ObservableProperty]
    private BranchSummary _branch = branch;

    public ObservableCollection<BranchTreeNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    private bool _isSelected;
}
