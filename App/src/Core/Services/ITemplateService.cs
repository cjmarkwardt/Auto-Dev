using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

/// <summary>Registry of workspace-scaffolding templates - plain `.md` files elsewhere on disk that describe how a workspace should be organized/configured (see TemplatesDialogViewModel). Only the registry (which paths are registered) is persisted; a template's own content is always read fresh at the moment it's applied, never cached, so editing the file takes effect immediately.</summary>
public interface ITemplateService
{
    /// <summary>Every registered template, filtered to files that still exist - same Directory.Exists-style filtering IWorkspaceService.GetRecentWorkspacesAsync already applies to its own list.</summary>
    Task<IReadOnlyList<WorkspaceTemplate>> GetRegisteredTemplatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Registers `path` if it isn't already registered - a no-op otherwise.</summary>
    Task RegisterTemplateAsync(string path, CancellationToken cancellationToken = default);

    Task UnregisterTemplateAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Reads `path` fresh from disk - deliberately never cached, so a template edited after being registered is picked up the very next time it's applied.</summary>
    Task<string> ReadTemplateContentAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Where the "Add Template" file picker starts browsing by default - `Templates` under AutoDev's own app data folder, created if it doesn't exist yet. Templates aren't required to live here; this is just a convenient default location, the same idea as IWorkspaceService.GetLastParentFolderAsync for the workspace folder picker.</summary>
    string GetDefaultTemplatesDirectory();
}
