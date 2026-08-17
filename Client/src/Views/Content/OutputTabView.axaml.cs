using Avalonia.Controls;
using AutoDev.ViewModels.Content;

namespace AutoDev.Views.Content;

/// <summary>
/// Grid.RowDefinitions/ColumnDefinitions have no bindable setter in Avalonia (only the XAML-literal string
/// form works), and an ItemsControl's generated containers can't have Grid.Row/Grid.Column data-bound onto
/// them directly either - so both are applied here in code-behind instead, driven by
/// OutputTabViewModel.GridRowCount/GridColumnCount and each ScriptBlockPanelViewModel's own
/// ResolvedRow/ResolvedColumn (see ScriptBlockGridLayout). Every application is a full pass over every
/// currently-realized container (not just the one that just changed) - a single recompute can shift several
/// panels' positions at once (e.g. the auto-square path renumbers everyone when the count changes), so
/// tracking "which one specifically moved" is more fragile than just reapplying everything each time.
/// </summary>
public partial class OutputTabView : UserControl
{
    private ItemsControl? _scriptBlockGrid;
    private Grid? _panel;

    public OutputTabView()
    {
        InitializeComponent();
        _scriptBlockGrid = this.FindControl<ItemsControl>("ScriptBlockGrid");
        if (_scriptBlockGrid is not null)
        {
            _scriptBlockGrid.ContainerPrepared += (_, _) => ApplyGridLayout();
        }

        DataContextChanged += OnDataContextChanged;
    }

    private OutputTabViewModel? Vm => DataContext as OutputTabViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        Vm.GridLayoutChanged += ApplyGridLayout;
        ApplyGridLayout();
    }

    private void ApplyGridLayout()
    {
        _panel ??= _scriptBlockGrid?.ItemsPanelRoot as Grid;
        if (_panel is null || Vm is null || _scriptBlockGrid is null)
        {
            return;
        }

        _panel.RowDefinitions.Clear();
        for (var i = 0; i < Vm.GridRowCount; i++)
        {
            _panel.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        }

        _panel.ColumnDefinitions.Clear();
        for (var i = 0; i < Vm.GridColumnCount; i++)
        {
            _panel.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        for (var i = 0; i < Vm.VisibleScriptBlocks.Count; i++)
        {
            if (_scriptBlockGrid.ContainerFromIndex(i) is not { } container)
            {
                continue; // not realized yet - its own ContainerPrepared will trigger a full pass that covers it
            }

            var panel = Vm.VisibleScriptBlocks[i];
            Grid.SetRow(container, panel.ResolvedRow);
            Grid.SetColumn(container, panel.ResolvedColumn);
        }
    }
}
