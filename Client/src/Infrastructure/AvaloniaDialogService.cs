using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.Views.Dialogs;
using AutoDev.ViewModels.Dialogs;
using AutoDev.ViewModels.Infrastructure;

namespace AutoDev.Infrastructure;

public sealed class AvaloniaDialogService(ITemplateService templateService) : IDialogService
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

    public async Task<string?> PickFileAsync(string title, IReadOnlyList<string> extensions, string? startDirectory = null)
    {
        var suggestedStartLocation = startDirectory is null
            ? null
            : await OwnerWindow.StorageProvider.TryGetFolderFromPathAsync(startDirectory);

        var files = await OwnerWindow.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = suggestedStartLocation,
            FileTypeFilter = [new FilePickerFileType(string.Join('/', extensions)) { Patterns = [.. extensions.Select(e => $"*{e}")] }],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
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

    public async Task<SquashDialogResult?> ShowSquashDialogAsync(IReadOnlyList<string> branches, Func<string, Task<string>> defaultMessageProvider)
    {
        var vm = new SquashDialogViewModel(branches, defaultMessageProvider);
        var window = new SquashDialogWindow { DataContext = vm };
        return await window.ShowDialog<SquashDialogResult?>(OwnerWindow);
    }

    public async Task<RebaseDialogResult?> ShowRebaseDialogAsync(IReadOnlyList<string> branches, Func<string, Task<string>> defaultMessageProvider)
    {
        var vm = new RebaseDialogViewModel(branches, defaultMessageProvider);
        var window = new RebaseDialogWindow { DataContext = vm };
        return await window.ShowDialog<RebaseDialogResult?>(OwnerWindow);
    }

    public async Task<MergeDialogResult?> ShowMergeDialogAsync(IReadOnlyList<string> branches, Func<string, Task<string>> defaultMessageProvider)
    {
        var vm = new MergeDialogViewModel(branches, defaultMessageProvider);
        var window = new MergeDialogWindow { DataContext = vm };
        return await window.ShowDialog<MergeDialogResult?>(OwnerWindow);
    }

    public async Task ShowMessageDialogAsync(string title, string message)
    {
        var vm = new MessageDialogViewModel { Title = title, Message = message };
        var window = new MessageDialogWindow { DataContext = vm };
        await window.ShowDialog(OwnerWindow);
    }

    public async Task<GitIdentityDialogResult?> ShowGitIdentityDialogAsync()
    {
        var vm = new GitIdentityDialogViewModel();
        var window = new GitIdentityDialogWindow { DataContext = vm };
        return await window.ShowDialog<GitIdentityDialogResult?>(OwnerWindow);
    }

    public async Task<AppliedTemplate?> ShowTemplatesDialogAsync(bool canApply)
    {
        var vm = new TemplatesDialogViewModel(templateService, this, canApply);
        var window = new TemplatesDialogWindow { DataContext = vm };
        return await window.ShowDialog<AppliedTemplate?>(OwnerWindow);
    }

}
