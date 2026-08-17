using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using AutoDev.Views.Dialogs;
using AutoDev.ViewModels.Dialogs;
using AutoDev.ViewModels.Infrastructure;

namespace AutoDev.Infrastructure;

public sealed class AvaloniaDialogService : IDialogService
{
    private static Window OwnerWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
        ?? throw new InvalidOperationException("Main window is not available yet.");

    public async Task<string?> PickFolderAsync(string? startDirectory = null)
    {
        var suggestedStartLocation = startDirectory is null
            ? null
            : await OwnerWindow.StorageProvider.TryGetFolderFromPathAsync(startDirectory);

        var folders = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Workspace Folder",
            AllowMultiple = false,
            SuggestedStartLocation = suggestedStartLocation,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> ShowInputDialogAsync(string title, string label, string initialValue = "", bool requireValue = false)
    {
        var vm = new InputDialogViewModel { Title = title, Label = label, Value = initialValue, RequireValue = requireValue };
        var window = new InputDialogWindow { DataContext = vm };
        return await window.ShowDialog<string?>(OwnerWindow);
    }

    public async Task<bool> ShowConfirmDialogAsync(string title, string message, string confirmLabel = "Delete", bool isDestructive = true)
    {
        var vm = new ConfirmDialogViewModel { Title = title, Message = message, ConfirmLabel = confirmLabel, IsDestructive = isDestructive };
        var window = new ConfirmDialogWindow { DataContext = vm };
        return await window.ShowDialog<bool>(OwnerWindow);
    }

    public async Task<CreateBranchDialogResult?> ShowCreateBranchDialogAsync()
    {
        var vm = new CreateBranchDialogViewModel();
        var window = new CreateBranchDialogWindow { DataContext = vm };
        return await window.ShowDialog<CreateBranchDialogResult?>(OwnerWindow);
    }

    public async Task<CreateTagDialogResult?> ShowCreateTagDialogAsync()
    {
        var vm = new CreateTagDialogViewModel();
        var window = new CreateTagDialogWindow { DataContext = vm };
        return await window.ShowDialog<CreateTagDialogResult?>(OwnerWindow);
    }
}
