using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using AutoDev.Core.Models;
using AutoDev.ViewModels;
using AutoDev.ViewModels.Content;

namespace AutoDev.Views.Content;

public partial class HistoryTabView : UserControl
{
    public HistoryTabView()
    {
        InitializeComponent();
    }

    private HistoryTabViewModel? Vm => DataContext as HistoryTabViewModel;

    private void OnBranchPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is StyledElement { DataContext: BranchTreeNodeViewModel node } && Vm is { } vm)
        {
            vm.SelectedBranchId = node.Branch.Id;
        }
    }

    /// <summary>A Commit or Tag row's click toggles its commit's expanded changes view in place; a ParentLink/ChildLink row's click still navigates directly, exactly as before - see HistoryTabViewModel.ToggleExpandedCommand/NavigateToCommand.</summary>
    private void OnEntryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not StyledElement { DataContext: TimelineEntryViewModel entry } || Vm is not { } vm)
        {
            return;
        }

        switch (entry.Entry.Kind)
        {
            case BranchTimelineEntryKind.ParentLink or BranchTimelineEntryKind.ChildLink when entry.Entry.LinkedBranchId is { } branchId:
                if (vm.NavigateToCommand.CanExecute(branchId))
                {
                    vm.NavigateToCommand.Execute(branchId);
                }

                break;
            case BranchTimelineEntryKind.Commit or BranchTimelineEntryKind.Tag:
                if (vm.ToggleExpandedCommand.CanExecute(entry))
                {
                    vm.ToggleExpandedCommand.Execute(entry);
                }

                break;
        }
    }

    /// <summary>A row in an expanded commit's changes tree - opens the clicked file's before/after diff (a no-op for a folder row - see HistoryTabViewModel.OpenChangeCommand).</summary>
    private void OnChangePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is StyledElement { DataContext: ChangeTreeNode node } && Vm is { } vm && vm.OpenChangeCommand.CanExecute(node))
        {
            vm.OpenChangeCommand.Execute(node);
        }
    }
}
