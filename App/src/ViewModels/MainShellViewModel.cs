using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AutoDev.ViewModels;

public sealed partial class MainShellViewModel : ViewModelBase
{
    private readonly IWorkspaceFactory workspaceFactory;
    private readonly IDialogService dialogService;
    private readonly ILogger<MainShellViewModel> logger;

    public MainShellViewModel(HeaderViewModel header, IWorkspaceFactory workspaceFactory, IDialogService dialogService, ILogger<MainShellViewModel> logger)
    {
        Header = header;
        this.workspaceFactory = workspaceFactory;
        this.dialogService = dialogService;
        this.logger = logger;
        Header.WorkspaceOpened += OnWorkspaceOpened;
    }

    public HeaderViewModel Header { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private WorkspaceViewModel? workspace;

    /// <summary>Bound to MainWindow's own Title - what window managers/task switchers show for this process, distinct from the title bar's own in-app workspace-name display.</summary>
    public string WindowTitle => Workspace?.Title ?? "AutoDev";

    /// <summary>Activates the workspace once it becomes the app's single open one - see WorkspaceViewModel.SetActive.</summary>
    partial void OnWorkspaceChanged(WorkspaceViewModel? oldValue, WorkspaceViewModel? newValue)
    {
        oldValue?.SetActive(false);
        newValue?.SetActive(true);
    }

    public async Task InitializeAsync()
    {
        await Header.RefreshAccountAsync();
        await Header.RefreshRecentWorkspacesAsync();
    }

    /// <summary>Title bar's Templates icon button - opens the register/remove/apply popup; Apply only shows while a workspace is actually open (see TemplatesDialogViewModel.CanApply), targeting whichever one that is.</summary>
    [RelayCommand]
    private async Task OpenTemplatesAsync()
    {
        if (await dialogService.ShowTemplatesDialogAsync(canApply: Workspace is not null) is { } applied && Workspace is not null)
        {
            await Workspace.ApplyTemplateAsync(applied.Name, applied.Content);
        }
    }

    private async void OnWorkspaceOpened(WorkspaceInfo workspace)
    {
        if (Workspace is { } existing)
        {
            if (existing.Workspace.FullPath == workspace.FullPath)
            {
                return;
            }

            try
            {
                await existing.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to fully dispose workspace {WorkspacePath} while replacing it", existing.Workspace.FullPath);
            }
        }

        WorkspaceViewModel opened = workspaceFactory.Create(workspace);
        Workspace = opened;
        await opened.InitializeAsync();
    }

    public async Task ShutdownAsync()
    {
        if (Workspace is not { } workspace)
        {
            return;
        }

        try
        {
            await workspace.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fully dispose workspace {WorkspacePath} during shutdown", workspace.Workspace.FullPath);
        }
    }
}
