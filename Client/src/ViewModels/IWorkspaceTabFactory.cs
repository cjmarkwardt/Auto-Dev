using AutoDev.AiCli;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Content;
using AutoDev.ViewModels.Infrastructure;
using AutoDev.ViewModels.Sidebar;
using Microsoft.Extensions.Logging;

namespace AutoDev.ViewModels;

public interface IWorkspaceTabFactory
{
    WorkspaceTabViewModel Create(WorkspaceInfo workspace);
}

/// <summary>
/// Composes a brand-new, fully isolated set of VM/service instances per workspace tab (own file watcher,
/// own task scheduler, own AI session client, own console state) - see plan doc's per-workspace
/// isolation design. Everything injected here is a stateless/shared singleton; the statefulness lives
/// entirely in the instances this factory creates.
/// </summary>
public sealed class WorkspaceTabFactory(
    IFileTreeService fileTreeService,
    IWorkspaceFileWatcherFactory watcherFactory,
    IDialogService dialogService,
    IUiDispatcher dispatcher,
    IWorkspaceMetadataStore metadataStore,
    ITaskSchedulerServiceFactory schedulerFactory,
    IVersioningServiceFactory versioningServiceFactory,
    IAiSessionClientFactory sessionClientFactory,
    IAiProviderSelectionService providerSelection,
    IUsageAggregatorService usageAggregator,
    IGitService gitService,
    ISoundService soundService,
    IExternalOpenService externalOpenService,
    IClipboardService clipboardService,
    ICommandExecutor commandExecutor,
    ILoggerFactory loggerFactory) : IWorkspaceTabFactory
{
    public WorkspaceTabViewModel Create(WorkspaceInfo workspace)
    {
        var versioningService = versioningServiceFactory.Create(workspace.FullPath);
        var scheduler = schedulerFactory.Create(workspace.FullPath);
        var edit = new EditTabViewModel(fileTreeService, externalOpenService);
        var files = new FilesSectionViewModel(workspace.FullPath, fileTreeService, watcherFactory, dialogService, dispatcher, externalOpenService, clipboardService, scheduler, versioningService, edit);

        var generate = new GenerateTabViewModel(
            workspace.FullPath,
            sessionClientFactory,
            providerSelection,
            metadataStore,
            usageAggregator,
            soundService,
            dispatcher,
            loggerFactory.CreateLogger<GenerateTabViewModel>());
        var version = new VersionSectionViewModel(versioningService, dialogService, generate, dispatcher);
        var history = new HistoryTabViewModel(versioningService, version, dialogService, edit);
        var output = new OutputTabViewModel(workspace.FullPath, metadataStore, scheduler, dispatcher);
        var command = new CommandTabViewModel(workspace.FullPath, commandExecutor, dispatcher);
        var content = new WorkspaceContentViewModel(edit, generate, history, output, command);
        var fileSearch = new FileSearchViewModel(workspace.FullPath, gitService);

        return new WorkspaceTabViewModel(workspace, version, files, content, fileSearch);
    }
}
