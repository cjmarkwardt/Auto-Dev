using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AutoDev.ViewModels.Content;

namespace AutoDev.Views.Content;

public partial class CommandTabView : UserControl
{
    private readonly ScrollViewer? scroller;
    private readonly TextBox? inputBox;

    /// <summary>The CommandTabViewModel OnVmPropertyChanged is currently subscribed to, if any - tracked so both OnDataContextChanged and DetachedFromVisualTree can unsubscribe it. Avalonia never recycles this view across a workspace-tab switch - a brand new CommandTabView is templated for whichever WorkspaceViewModel becomes selected, and the previous one is simply dropped, so DataContextChanged alone never fires again to clean it up (see WorkspaceView's own identical fix/doc comment) - without unsubscribing on detach too, every past tab switch leaves one more CommandTabView permanently reachable through its own workspace's long-lived CommandTabViewModel.</summary>
    private CommandTabViewModel? subscribedVm;

    public CommandTabView()
    {
        InitializeComponent();
        scroller = this.FindControl<ScrollViewer>("Scroller");
        inputBox = this.FindControl<TextBox>("CommandInput");
        if (inputBox is not null)
        {
            // Tunnel (not bubble): TextBox's own AcceptsReturn handling consumes Enter/Up/Down during the
            // bubble phase first, same reasoning as GenerateTabView.axaml.cs's identical setup.
            inputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        }

        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();

        if (DataContext is CommandTabViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            subscribedVm = vm;
        }
    }

    private void Unsubscribe()
    {
        if (subscribedVm is null)
        {
            return;
        }

        subscribedVm.PropertyChanged -= OnVmPropertyChanged;
        subscribedVm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not CommandTabViewModel vm)
        {
            return;
        }

        if (args.PropertyName == nameof(CommandTabViewModel.OutputText))
        {
            Dispatcher.UIThread.Post(() => scroller?.ScrollToEnd(), DispatcherPriority.Background);
        }

        // The input box is IsEnabled="{Binding !IsRunning}" (see CommandTabView.axaml) - Avalonia
        // drops keyboard focus the instant a focused control is disabled, and re-enabling it
        // afterward doesn't get that focus back on its own, so a submitted command would otherwise
        // leave the box sitting there enabled but unfocused until the user clicks back into it.
        else if (args.PropertyName == nameof(CommandTabViewModel.IsRunning) && !vm.IsRunning)
        {
            Dispatcher.UIThread.Post(() => inputBox?.Focus(), DispatcherPriority.Background);
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CommandTabViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (vm.RunCommand.CanExecute(null))
            {
                vm.RunCommand.Execute(null);
            }

            e.Handled = true;
            return;
        }

        if (sender is not TextBox box)
        {
            return;
        }

        if (e.Key == Key.Up && box.CaretIndex == 0)
        {
            vm.RecallPrevious();
            e.Handled = true;
        }
        else if (e.Key == Key.Down && box.CaretIndex == (box.Text?.Length ?? 0))
        {
            vm.RecallNext();
            e.Handled = true;
        }
    }
}
