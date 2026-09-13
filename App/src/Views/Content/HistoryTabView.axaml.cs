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
        if (sender is StyledElement { DataContext: BranchRowViewModel node } && Vm is { } vm && vm.SelectBranchCommand.CanExecute(node.Branch.Name))
        {
            vm.SelectBranchCommand.Execute(node.Branch.Name);
        }
    }

    /// <summary>The current branch's own row has nothing left to offer on its context menu - Checkout/Merge/Rebase/Delete only make sense for a different branch, and every current-branch-only action (Commit/Reset/Branch/Tag/Remote/Squash/Rebase) lives on the Version section instead. Suppresses the (otherwise empty) popup for that row rather than letting it open with nothing in it.</summary>
    private void OnBranchContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is StyledElement { DataContext: BranchRowViewModel { Branch.IsCurrent: true } })
        {
            e.Handled = true;
        }
    }

    /// <summary>A Commit or Tag row's left-click toggles its commit's expanded changes view in place - see HistoryTabViewModel.ToggleExpandedCommand. A right-click only opens the row's context menu (see HistoryTabView.axaml), not this.</summary>
    private void OnEntryPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is StyledElement { DataContext: TimelineEntryViewModel entry } && Vm is { } vm && vm.ToggleExpandedCommand.CanExecute(entry))
        {
            vm.ToggleExpandedCommand.Execute(entry);
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
