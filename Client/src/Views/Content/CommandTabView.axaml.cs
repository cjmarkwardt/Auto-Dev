using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AutoDev.ViewModels.Content;

namespace AutoDev.Views.Content;

public partial class CommandTabView : UserControl
{
    private readonly ScrollViewer? _scroller;
    private readonly TextBox? _inputBox;

    public CommandTabView()
    {
        InitializeComponent();
        _scroller = this.FindControl<ScrollViewer>("Scroller");
        _inputBox = this.FindControl<TextBox>("CommandInput");
        if (_inputBox is not null)
        {
            // Tunnel (not bubble): TextBox's own AcceptsReturn handling consumes Enter/Up/Down during the
            // bubble phase first, same reasoning as GenerateTabView.axaml.cs's identical setup.
            _inputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
        }

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is CommandTabViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CommandTabViewModel.OutputText))
                {
                    Dispatcher.UIThread.Post(() => _scroller?.ScrollToEnd(), DispatcherPriority.Background);
                }
            };
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
