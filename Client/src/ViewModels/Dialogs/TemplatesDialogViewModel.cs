using System.Collections.ObjectModel;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Dialogs;

/// <summary>Lists every registered scaffolding template, with Remove (unregister) and - only while CanApply - Apply (scaffold the currently open workspace) per row, plus an Add button that registers a new one via a file picker. See ITemplateService for why a template's content is never cached here.</summary>
public sealed partial class TemplatesDialogViewModel : ViewModelBase
{
    private readonly ITemplateService templateService;
    private readonly IDialogService dialogService;

    public TemplatesDialogViewModel(ITemplateService templateService, IDialogService dialogService, bool canApply)
    {
        this.templateService = templateService;
        this.dialogService = dialogService;
        CanApply = canApply;
        _ = RefreshAsync();
    }

    public ObservableCollection<WorkspaceTemplate> Templates { get; } = [];

    /// <summary>False when no workspace is currently open - Apply has nothing to scaffold then, so it's hidden entirely (see TemplatesDialogWindow.axaml) rather than shown disabled.</summary>
    public bool CanApply { get; }

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Non-null argument is the template that was applied (name plus its freshly-read content); null means the dialog was closed (Close button, or the window's own chrome) without applying one.</summary>
    public event Action<AppliedTemplate?>? RequestClose;

    private async Task RefreshAsync()
    {
        IsLoading = true;
        var templates = await templateService.GetRegisteredTemplatesAsync();
        Templates.Clear();
        foreach (var template in templates)
        {
            Templates.Add(template);
        }

        IsLoading = false;
    }

    [RelayCommand]
    private async Task AddTemplateAsync()
    {
        var path = await dialogService.PickFileAsync("Add Template", [".md"], templateService.GetDefaultTemplatesDirectory());
        if (path is null)
        {
            return;
        }

        await templateService.RegisterTemplateAsync(path);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RemoveAsync(WorkspaceTemplate template)
    {
        await templateService.UnregisterTemplateAsync(template.Path);
        await RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(WorkspaceTemplate template)
    {
        var content = await templateService.ReadTemplateContentAsync(template.Path);
        RequestClose?.Invoke(new AppliedTemplate { Name = template.Name, Content = content });
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke(null);
}
