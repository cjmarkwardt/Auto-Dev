using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using AutoDev.ViewModels;
using AutoDev.ViewModels.Sidebar;

namespace AutoDev.Views.Sidebar;

public partial class FilesSectionView : UserControl
{
    /// <summary>How far the pointer must move (in DIPs) past a row's PointerPressed before it counts as a drag rather than a click - keeps an ordinary click/select from ever misfiring DoDragDropAsync.</summary>
    private static readonly double dragStartThreshold = 4;

    private FileTreeNodeViewModel? dragCandidateNode;
    private PointerPressedEventArgs? dragPressArgs;
    private Point dragStartPosition;

    public FilesSectionView()
    {
        InitializeComponent();
    }

    private void OnNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: FileTreeNodeViewModel node } element)
        {
            return;
        }

        if (!node.IsPlaceholder && e.GetCurrentPoint(element).Properties.IsLeftButtonPressed)
        {
            dragCandidateNode = node;
            dragPressArgs = e;
            dragStartPosition = e.GetPosition(element);
        }

        if (!node.IsDirectory && DataContext is FilesSectionViewModel vm)
        {
            vm.ActivateFile(node.FullPath);

            if (e.ClickCount == 2 && node.IsRunnableFile && vm.RunScriptCommand.CanExecute(node))
            {
                vm.RunScriptCommand.Execute(node);
            }
        }
    }

    /// <summary>
    /// Starts an internal row drag once the pointer has moved far enough with the button still held - reuses
    /// the exact same DataFormat.File payload an external OS drag would carry, so OnRowDrop/OnTreeDrop (and
    /// their existing MoveExternalItemsAsync call) handle a drag started here identically, with no drop-side
    /// changes needed at all.
    /// </summary>
    private async void OnRowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragCandidateNode is not { } node || dragPressArgs is not { } pressArgs || sender is not Control element)
        {
            return;
        }

        if (!e.GetCurrentPoint(element).Properties.IsLeftButtonPressed)
        {
            dragCandidateNode = null;
            dragPressArgs = null;
            return;
        }

        if (Point.Distance(e.GetPosition(element), dragStartPosition) < dragStartThreshold)
        {
            return;
        }

        dragCandidateNode = null;
        dragPressArgs = null;

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storageProvider)
        {
            return;
        }

        Uri uri = new Uri(node.FullPath);
        IStorageItem? item = node.IsDirectory
            ? await storageProvider.TryGetFolderFromPathAsync(uri)
            : await storageProvider.TryGetFileFromPathAsync(uri);

        if (item is null)
        {
            return;
        }

        DataTransfer dataTransfer = new DataTransfer();
        dataTransfer.Add(DataTransferItem.CreateFile(item));
        await DragDrop.DoDragDropAsync(pressArgs, dataTransfer, DragDropEffects.Move);
    }

    private void OnRowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        dragCandidateNode = null;
        dragPressArgs = null;
    }

    private void OnRowDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>A file row resolves to its own containing folder (see the XAML comment above); a directory row is the target directory itself.</summary>
    private async void OnRowDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not StyledElement { DataContext: FileTreeNodeViewModel node } || DataContext is not FilesSectionViewModel vm)
        {
            return;
        }

        string? targetDirectory = node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath);
        if (targetDirectory is null)
        {
            return;
        }

        List<string> paths = GetDroppedPaths(e);
        if (paths.Count > 0)
        {
            await vm.MoveExternalItemsAsync(paths, targetDirectory);
        }
    }

    private void OnTreeDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Move : DragDropEffects.None;

    private async void OnTreeDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not FilesSectionViewModel vm)
        {
            return;
        }

        List<string> paths = GetDroppedPaths(e);
        if (paths.Count > 0)
        {
            await vm.MoveExternalItemsAsync(paths, vm.RootPath);
        }
    }

    private static List<string> GetDroppedPaths(DragEventArgs e)
    {
        List<string> paths = new List<string>();
        foreach (IStorageItem item in e.DataTransfer.TryGetFiles() ?? [])
        {
            if (item.TryGetLocalPath() is { } path)
            {
                paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>A row in the Changes Mode tree - opens the clicked file's before/after diff (a no-op for a folder row - see FilesSectionViewModel.OpenChangeCommand).</summary>
    private void OnChangePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is StyledElement { DataContext: ChangeTreeNode node } && DataContext is FilesSectionViewModel vm && vm.OpenChangeCommand.CanExecute(node))
        {
            vm.OpenChangeCommand.Execute(node);
        }
    }
}
