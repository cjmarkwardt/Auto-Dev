namespace AutoDev.Core.Models;

public sealed class AppSettings
{
    public List<string> RecentWorkspacePaths { get; set; } = [];

    /// <summary>Absolute paths of every registered scaffolding template - see ITemplateService. A template's content is never stored here, only where to find it.</summary>
    public List<string> TemplatePaths { get; set; } = [];

    /// <summary>The AI provider last selected via the title bar's provider switcher (see AiProviderSelectionService) - the string form of AiProvider, e.g. "Claude"/"Codex". Null (or any value that doesn't parse) falls back to AiProvider.Claude.</summary>
    public string? AiProvider { get; set; }

    /// <summary>The parent directory the folder picker was last browsed into for "Open Folder" or "Clone Repository" (see HeaderViewModel.BrowseForFolderAsync/CloneAsync) - null (or a path that no longer exists) falls back to the picker's own platform default start location.</summary>
    public string? LastParentFolderPath { get; set; }
}
