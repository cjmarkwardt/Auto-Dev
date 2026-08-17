using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace AutoDev.Views;

public partial class AuthGateView : UserControl
{
    public AuthGateView()
    {
        InitializeComponent();
    }

    /// <summary>See MainShellView's OnTitleBarPointerPressed for why the interactive-descendant check is needed.</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual sourceVisual && sourceVisual.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this) is Window window)
        {
            window.BeginMoveDrag(e);
        }
    }
}
