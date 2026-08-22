using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AutoDev.ViewModels;
using AutoDev.ViewModels.Sidebar;

namespace AutoDev.Views;

public partial class WorkspaceTabView : UserControl
{
    private readonly TextBox? _searchBox;
    private readonly ScrollViewer? _gitLogScroll;
    private readonly SelectableTextBlock? _gitLogText;

    /// <summary>The FileSearchViewModel OnFileSearchPropertyChanged is currently subscribed to, if any - tracked so OnDataContextChanged can unsubscribe it before subscribing to whatever replaces it. Without this, a DataContext change (e.g. Avalonia recycling this view across a tab switch) left the old subscription attached forever; if DataContext later went null on this same instance while an old FileSearchViewModel it was never unsubscribed from opened, the handler still fired here and crashed the whole app dereferencing a null Vm.</summary>
    private FileSearchViewModel? _subscribedFileSearch;

    /// <summary>The VersionSectionViewModel OnGitOutputLogChanged is currently subscribed to, if any - same leak-prevention reasoning as _subscribedFileSearch.</summary>
    private VersionSectionViewModel? _subscribedVersion;

    public WorkspaceTabView()
    {
        InitializeComponent();
        _searchBox = this.FindControl<TextBox>("SearchBox");
        _gitLogScroll = this.FindControl<ScrollViewer>("GitLogScroll");
        _gitLogText = this.FindControl<SelectableTextBlock>("GitLogText");
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

        if (_subscribedVersion is not null)
        {
            _subscribedVersion.GitOutputLog.CollectionChanged -= OnGitOutputLogChanged;
            _subscribedVersion.PropertyChanged -= OnVersionPropertyChanged;
            _subscribedVersion = null;
        }

        if (Vm is { } vm)
        {
            vm.FileSearch.PropertyChanged += OnFileSearchPropertyChanged;
            _subscribedFileSearch = vm.FileSearch;

            vm.Version.GitOutputLog.CollectionChanged += OnGitOutputLogChanged;
            vm.Version.PropertyChanged += OnVersionPropertyChanged;
            _subscribedVersion = vm.Version;
        }
    }

    /// <summary>Keeps the busy overlay's live git command log scrolled to its newest line as more arrive.</summary>
    private void OnGitOutputLogChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => _gitLogScroll?.ScrollToEnd(), DispatcherPriority.Background);

    /// <summary>
    /// Focuses the busy overlay's own git log text the moment it appears (IsBusy flips true) - Avalonia never
    /// auto-focuses content just because its IsVisible binding turns true, so without this, whatever had focus
    /// before the overlay opened (a sidebar button, a menu item, ...) kept it, and Ctrl+C/Ctrl+A on the log
    /// silently did nothing despite a mouse-drag selection still working (SelectableTextBlock tracks selection
    /// state independently of logical focus, but its own keyboard shortcuts only fire once it's the focused
    /// element) - same reasoning as OnFileSearchPropertyChanged's _searchBox?.Focus() for the F2 popup.
    /// </summary>
    private void OnVersionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VersionSectionViewModel.IsBusy) && sender is VersionSectionViewModel { IsBusy: true })
        {
            Dispatcher.UIThread.Post(() => _gitLogText?.Focus(), DispatcherPriority.Background);
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

    /// <summary>The busy overlay's own explicit copy button - copies whatever's currently selected within the
    /// git log, or the whole log (SelectAll first) if nothing is, so a click always copies something useful
    /// regardless of whether the user bothered to select anything first, or whether the text currently holds
    /// keyboard focus at all (SelectableTextBlock.Copy works off its own selection state directly, unlike the
    /// Ctrl+C keybinding, which needs the control to actually have focus).</summary>
    private void OnCopyGitLogClick(object? sender, RoutedEventArgs e)
    {
        if (_gitLogText is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(_gitLogText.SelectedText))
        {
            _gitLogText.SelectAll();
        }

        _gitLogText.Copy();
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
