using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AutoDev.ViewModels.Content;

namespace AutoDev.Views.Content;

public partial class ScriptTabView : UserControl
{
    /// <summary>How close to the bottom (in pixels) still counts as "at the bottom" for IsScrolledToBottom - a small allowance for sub-pixel/rounding slack in ScrollViewer's own Offset/Extent/Viewport, rather than demanding exact equality.</summary>
    private const double BottomTolerance = 2.0;

    private readonly ScrollViewer? _scroller;
    private readonly TextBox? _inputBox;

    /// <summary>The ScriptTabViewModel OnVmPropertyChanged is currently subscribed to, if any - tracked so both OnDataContextChanged and DetachedFromVisualTree can unsubscribe it. Avalonia never recycles this view across a workspace-tab switch - a brand new ScriptTabView is templated for whichever WorkspaceViewModel becomes selected, and the previous one is simply dropped, so DataContextChanged alone never fires again to clean it up (see WorkspaceView's own identical fix/doc comment) - without unsubscribing on detach too, every past tab switch leaves one more ScriptTabView permanently reachable through its own workspace's long-lived ScriptTabViewModel.</summary>
    private ScriptTabViewModel? _subscribedVm;

    /// <summary>
    /// Whether _scroller was sitting at (or within BottomTolerance of) the bottom the last time its own
    /// scroll position was observed - kept continuously up to date by OnScrollerScrollChanged (which fires
    /// for a ScrollToEnd() call below just as much as a manual scroll), rather than re-derived from
    /// _scroller's current Offset/Extent/Viewport at the moment new output arrives: those would still reflect
    /// the *previous* layout pass at that exact point (the new text hasn't been measured in yet), and if a
    /// previous update's own ScrollToEnd() is still queued (Dispatcher.Post below never runs synchronously),
    /// re-checking there could see a stale, not-yet-applied offset and wrongly conclude the user had scrolled
    /// away. Starts true so the first output a freshly selected/started script produces still scrolls into
    /// view immediately.
    /// </summary>
    private bool _isScrolledToBottom = true;

    public ScriptTabView()
    {
        InitializeComponent();
        _scroller = this.FindControl<ScrollViewer>("Scroller");
        _inputBox = this.FindControl<TextBox>("InputBox");
        if (_scroller is not null)
        {
            _scroller.ScrollChanged += OnScrollerScrollChanged;
        }

        if (_inputBox is not null)
        {
            // Tunnel (not bubble): TextBox's own handling would otherwise consume Enter first during the
            // bubble phase - same reasoning as CommandTabView/GenerateTabView's identical setup.
            _inputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        }

        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnScrollerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Ignored when only ExtentDelta is nonzero: streamed-in output growing the content below the
        // current viewport fires this same event with the offset untouched, and re-checking against that
        // still-unmoved offset against the now-taller extent would immediately (and wrongly) read as
        // "scrolled away" the very first time output overflows the viewport - before ScrollToEnd() below
        // ever gets a chance to run - permanently latching auto-scroll off with no real scroll from the
        // user. Only an actual offset change (a manual scroll, or our own ScrollToEnd() call) means anything
        // here.
        if (_scroller is null || e.OffsetDelta.Y == 0)
        {
            return;
        }

        _isScrolledToBottom = _scroller.Offset.Y + _scroller.Viewport.Height >= _scroller.Extent.Height - BottomTolerance;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();

        if (DataContext is ScriptTabViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            _subscribedVm = vm;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribedVm is null)
        {
            return;
        }

        _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
        _subscribedVm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ScriptTabViewModel.OutputText))
        {
            return;
        }

        // The bottom check is deliberately re-evaluated inside the posted callback rather than here: output
        // can arrive fast enough for several of these to queue up on the Dispatcher before the UI thread
        // catches up, and checking only at queue time would let an already-queued ScrollToEnd() fire after
        // the user has since scrolled away, yanking them back down out from under whatever they're reading.
        Dispatcher.UIThread.Post(() =>
        {
            if (_isScrolledToBottom)
            {
                _scroller?.ScrollToEnd();
            }
        }, DispatcherPriority.Background);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ScriptTabViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (vm.SendInputCommand.CanExecute(null))
            {
                vm.SendInputCommand.Execute(null);
            }

            e.Handled = true;
        }
    }
}
