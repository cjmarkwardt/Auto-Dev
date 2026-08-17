using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AutoDev.Views;

public partial class WindowControlsView : UserControl
{
    private Button? _maximizeButton;
    private Button? _restoreButton;
    private Window? _subscribedWindow;

    public WindowControlsView()
    {
        InitializeComponent();
        _maximizeButton = this.FindControl<Button>("MaximizeButton");
        _restoreButton = this.FindControl<Button>("RestoreButton");
        AttachedToVisualTree += (_, _) => SubscribeToWindowState();
        DetachedFromVisualTree += (_, _) => UnsubscribeFromWindowState();
    }

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private void SubscribeToWindowState()
    {
        UnsubscribeFromWindowState();
        if (OwnerWindow is { } window)
        {
            _subscribedWindow = window;
            window.PropertyChanged += OnWindowPropertyChanged;
            UpdateMaximizeGlyph(window.WindowState);
        }
    }

    private void UnsubscribeFromWindowState()
    {
        if (_subscribedWindow is { } window)
        {
            window.PropertyChanged -= OnWindowPropertyChanged;
            _subscribedWindow = null;
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
        var isMaximized = state == WindowState.Maximized;
        if (_maximizeButton is not null)
        {
            _maximizeButton.IsVisible = !isMaximized;
        }

        if (_restoreButton is not null)
        {
            _restoreButton.IsVisible = isMaximized;
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
