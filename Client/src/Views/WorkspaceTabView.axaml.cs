using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AutoDev.ViewModels;
using AutoDev.ViewModels.Sidebar;

namespace AutoDev.Views;

public partial class WorkspaceTabView : UserControl
{
    private readonly TextBox? _searchBox;

    /// <summary>The FileSearchViewModel OnFileSearchPropertyChanged is currently subscribed to, if any - tracked so OnDataContextChanged can unsubscribe it before subscribing to whatever replaces it. Without this, a DataContext change (e.g. Avalonia recycling this view across a tab switch) left the old subscription attached forever; if DataContext later went null on this same instance while an old FileSearchViewModel it was never unsubscribed from opened, the handler still fired here and crashed the whole app dereferencing a null Vm.</summary>
    private FileSearchViewModel? _subscribedFileSearch;

    public WorkspaceTabView()
    {
        InitializeComponent();
        _searchBox = this.FindControl<TextBox>("SearchBox");
        DataContextChanged += OnDataContextChanged;
    }

    private WorkspaceTabViewModel? Vm => DataContext as WorkspaceTabViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_subscribedFileSearch is not null)
        {
            _subscribedFileSearch.PropertyChanged -= OnFileSearchPropertyChanged;
            _subscribedFileSearch = null;
        }

        if (Vm is { } vm)
        {
            vm.FileSearch.PropertyChanged += OnFileSearchPropertyChanged;
            _subscribedFileSearch = vm.FileSearch;
        }
    }

    /// <summary>Reads IsOpen off `sender` (guaranteed to be the exact FileSearchViewModel that raised this) rather than Vm.FileSearch - Vm re-reads DataContext live, which is exactly what could go stale/null out from under this handler (see _subscribedFileSearch's doc comment).</summary>
    private void OnFileSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileSearchViewModel.IsOpen) && sender is FileSearchViewModel { IsOpen: true })
        {
            Dispatcher.UIThread.Post(() =>
            {
                _searchBox?.Focus();
                _searchBox?.SelectAll();
            }, DispatcherPriority.Background);
        }
    }

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e) => Vm?.FileSearch.Close();

    private void OnPopupPointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private void OnResultPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is StyledElement { DataContext: FileSearchResultViewModel result })
        {
            Vm?.FileSearch.Choose(result);
        }
    }

    private void OnContentResultPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is StyledElement { DataContext: ContentSearchResultViewModel result })
        {
            Vm?.FileSearch.ChooseContent(result);
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                Vm.FileSearch.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                Vm.FileSearch.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                Vm.FileSearch.ChooseSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                Vm.FileSearch.Close();
                e.Handled = true;
                break;
        }
    }
}
