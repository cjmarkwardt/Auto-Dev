namespace AutoDev.Core.Models;

public sealed class AppSettings
{
    public List<string> RecentWorkspacePaths { get; set; } = [];

    /// <summary>Exactly the workspace tabs open when the app last closed - distinct from RecentWorkspacePaths (an MRU picker list) - restored as tabs on the next launch (see WorkspaceService.GetOpenWorkspacesAsync/MainShellViewModel), skipping any whose folder no longer exists.</summary>
    public List<string> OpenWorkspacePaths { get; set; } = [];

    /// <summary>The AI provider last selected via the title bar's provider switcher (see AiProviderSelectionService) - the string form of AiProvider, e.g. "Claude"/"Codex". Null (or any value that doesn't parse) falls back to AiProvider.Claude.</summary>
    public string? AiProvider { get; set; }

    /// <summary>The parent directory the folder picker was last browsed into for "Open Folder" or "Clone Repository" (see HeaderViewModel.BrowseForFolderAsync/CloneAsync) - null (or a path that no longer exists) falls back to the picker's own platform default start location.</summary>
    public string? LastParentFolderPath { get; set; }
}
