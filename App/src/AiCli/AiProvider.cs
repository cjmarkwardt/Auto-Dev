namespace AutoDev.AiCli;

/// <summary>Which AI CLI backs the app's Generate tab - see IAiProviderSelectionService for the app-wide current choice.</summary>
public enum AiProvider
{
    Claude,
    Codex,
}

/// <summary><see cref="AiProvider"/> presentation helpers - kept next to the enum since every provider gets a case here as it's added, same as any other exhaustive switch over this type.</summary>
public static class AiProviderExtensions
{
    public static string DisplayName(this AiProvider provider) => provider switch
    {
        AiProvider.Claude => "Claude",
        AiProvider.Codex => "Codex",
        _ => provider.ToString(),
    };
}
