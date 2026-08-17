using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using AutoDev.AiCli;
using AutoDev.Core.Models;
using AutoDev.ViewModels;

namespace AutoDev.Views;

public partial class MainShellView : UserControl
{
    public MainShellView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// The header IS the window's titlebar now (no OS chrome - see MainWindow's ExtendClientArea* hints),
    /// so clicks on its empty background drag/double-click-maximize the window like a real titlebar would.
    /// Interactive children (buttons, tab items) do NOT mark PointerPressed Handled during the press phase
    /// (only their eventual Click does, on release) - dragging unconditionally on every bubbled press would
    /// hijack pointer capture out from under them before that Click ever fires. So this explicitly skips
    /// dragging/maximizing whenever the press originated from an interactive descendant.
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual sourceVisual &&
            (sourceVisual.FindAncestorOfType<Button>(includeSelf: true) is not null ||
             sourceVisual.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not null))
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is not Window window)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        window.BeginMoveDrag(e);
    }

    private void OnRecentBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainShellViewModel vm)
        {
            vm.Header.CloseRecentMenu();
        }
    }

    /// <summary>Stops a click inside the dropdown's own body from bubbling to the backdrop and closing it.</summary>
    private void OnRecentPopupPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnRecentRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // The remove (x) button lives inside this same row - let its own Click/Command handle that case
        // instead of also opening the workspace it just removed.
        if (e.Source is Visual sourceVisual && sourceVisual.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (sender is StyledElement { DataContext: WorkspaceInfo workspace } && DataContext is MainShellViewModel vm)
        {
            vm.Header.OpenRecentCommand.Execute(workspace);
        }
    }

    private void OnProviderBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainShellViewModel vm)
        {
            vm.Header.CloseProviderMenu();
        }
    }

    /// <summary>Stops a click inside the dropdown's own body from bubbling to the backdrop and closing it.</summary>
    private void OnProviderPopupPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnProviderRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (sender is StyledElement { DataContext: AiProvider provider } && DataContext is MainShellViewModel vm)
        {
            vm.Header.SelectProviderCommand.Execute(provider);
        }
    }
}
