using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AutoDev.Views;

public partial class WindowControlsView : UserControl
{
    private Button? maximizeButton;
    private Button? restoreButton;
    private Window? subscribedWindow;

    public WindowControlsView()
    {
        InitializeComponent();
        maximizeButton = this.FindControl<Button>("MaximizeButton");
        restoreButton = this.FindControl<Button>("RestoreButton");
        AttachedToVisualTree += (_, _) => SubscribeToWindowState();
        DetachedFromVisualTree += (_, _) => UnsubscribeFromWindowState();
    }

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private void SubscribeToWindowState()
    {
        UnsubscribeFromWindowState();
        if (OwnerWindow is { } window)
        {
            subscribedWindow = window;
            window.PropertyChanged += OnWindowPropertyChanged;
            UpdateMaximizeGlyph(window.WindowState);
        }
    }

    private void UnsubscribeFromWindowState()
    {
        if (subscribedWindow is { } window)
        {
            window.PropertyChanged -= OnWindowPropertyChanged;
            subscribedWindow = null;
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty)
        {
            UpdateMaximizeGlyph((WindowState)e.NewValue!);
        }
    }

    private void UpdateMaximizeGlyph(WindowState state)
    {
        bool isMaximized = state == WindowState.Maximized;
        if (maximizeButton is not null)
        {
            maximizeButton.IsVisible = !isMaximized;
        }

        if (restoreButton is not null)
        {
            restoreButton.IsVisible = isMaximized;
        }
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow is { } window)
        {
            window.WindowState = WindowState.Minimized;
        }
    }

    private void OnMaximizeRestoreClick(object? sender, RoutedEventArgs e)
    {
        if (OwnerWindow is not { } window)
        {
            return;
        }

        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => OwnerWindow?.Close();
}
