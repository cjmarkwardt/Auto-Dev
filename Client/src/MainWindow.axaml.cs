using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AutoDev.ViewModels;
using AutoDev.ViewModels.Content;

namespace AutoDev;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        WindowState = WindowState.Maximized;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var tab = Vm?.Shell.SelectedTab;
        if (tab is null)
        {
            return;
        }

        if (tab.Version.IsBusy)
        {
            // A git action is in flight - the loading overlay is covering the screen, but keyboard focus can
            // still sit on an underlying control regardless of z-order, so swallow every key here too.
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.F1:
                // First press opens file search (filename mode); pressing it again while already open
                // toggles into content search, and again back to filename - see FileSearchViewModel.ToggleMode.
                if (tab.FileSearch.IsOpen)
                {
                    tab.FileSearch.ToggleMode();
                }
                else
                {
                    tab.FileSearch.Open();
                }

                e.Handled = true;
                return;
            case Key.F2:
                tab.Content.SelectedTabIndex = WorkspaceContentViewModel.GenerateTabIndex;
                e.Handled = true;
                return;
            case Key.F3:
                tab.Content.SelectedTabIndex = WorkspaceContentViewModel.HistoryTabIndex;
                e.Handled = true;
                return;
            case Key.F4:
                tab.Content.SelectedTabIndex = WorkspaceContentViewModel.OutputTabIndex;
                e.Handled = true;
                return;
            case Key.F5:
                tab.Content.SelectedTabIndex = WorkspaceContentViewModel.CommandTabIndex;
                e.Handled = true;
                return;
        }
    }
}
