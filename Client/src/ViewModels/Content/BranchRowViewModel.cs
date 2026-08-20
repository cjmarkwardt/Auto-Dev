using AutoDev.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoDev.ViewModels.Content;

/// <summary>One row in the History tab's flat branch list - a branch's own name plus whether it's the currently checked-out one, with this row's own selection state (which branch's timeline the right pane is currently showing).</summary>
public sealed partial class BranchRowViewModel(BranchSummary branch) : ViewModelBase
{
    [ObservableProperty]
    private BranchSummary _branch = branch;

    [ObservableProperty]
    private bool _isSelected;
}
