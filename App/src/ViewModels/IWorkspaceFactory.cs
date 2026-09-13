using AutoDev.AiCli;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Content;
using AutoDev.ViewModels.Infrastructure;
using AutoDev.ViewModels.Sidebar;
using Microsoft.Extensions.Logging;

namespace AutoDev.ViewModels;

public interface IWorkspaceFactory
{
    WorkspaceViewModel Create(WorkspaceInfo workspace);
}

/// <summary>
/// Composes a brand-new, fully isolated set of VM/service instances per workspace tab (own file watcher,
/// own script runner, own AI session client, own console state) - see plan doc's per-workspace
/// isolation design. Everything injected here is a stateless/shared singleton; the statefulness lives
/// entirely in the instances this factory creates.
/// </summary>
public sealed class WorkspaceFactory(
    IFileTreeService fileTreeService,
    IWorkspaceFileWatcherFactory watcherFactory,
    IDialogService dialogService,
    IUiDispatcher dispatcher,
    IWorkspaceMetadataStore metadataStore,
    IScriptRunnerServiceFactory scriptRunnerFactory,
    IVersioningServiceFactory versioningServiceFactory,
    IAiSessionClientFactory sessionClientFactory,
    IAiProviderSelectionService providerSelection,
    IUsageAggregatorService usageAggregator,
    IGitService gitService,
    ISoundService soundService,
    IExternalOpenService externalOpenService,
    IClipboardService clipboardService,
    ICommandExecutor commandExecutor,
    ILoggerFactory loggerFactory) : IWorkspaceFactory
{
    public WorkspaceViewModel Create(WorkspaceInfo workspace)
    {
        IWorkspaceVersioningService versioningService = versioningServiceFactory.Create(workspace.FullPath);
        IWorkspaceScriptRunner scriptRunner = scriptRunnerFactory.Create(workspace.FullPath);
        EditTabViewModel edit = new EditTabViewModel(fileTreeService, externalOpenService);
        FilesSectionViewModel files = new FilesSectionViewModel(workspace.FullPath, fileTreeService, watcherFactory, dialogService, dispatcher, externalOpenService, clipboardService, scriptRunner, versioningService, edit);

        GenerateTabViewModel generate = new GenerateTabViewModel(
            workspace.FullPath,
            sessionClientFactory,
            providerSelection,
            metadataStore,
            usageAggregator,
            soundService,
            dispatcher,
            loggerFactory.CreateLogger<GenerateTabViewModel>());
        VersionSectionViewModel version = new VersionSectionViewModel(versioningService, dialogService, generate, dispatcher);
        HistoryTabViewModel history = new HistoryTabViewModel(versioningService, version, dialogService, edit);
        ScriptTabViewModel script = new ScriptTabViewModel(workspace.FullPath, metadataStore, scriptRunner, clipboardService, dispatcher);
        CommandTabViewModel command = new CommandTabViewModel(workspace.FullPath, commandExecutor, dispatcher);
        WorkspaceContentViewModel content = new WorkspaceContentViewModel(edit, generate, history, script, command);
        FileSearchViewModel fileSearch = new FileSearchViewModel(workspace.FullPath, gitService, files);

        return new WorkspaceViewModel(workspace, version, files, content, fileSearch);
    }
}
