using AutoDev.AiCli;

namespace AutoDev.ClaudeCli;

/// <summary>
/// Resolves the `claude` executable name/path. Left as a thin seam (rather than a hardcoded literal)
/// so a future settings screen can point at a non-PATH install without touching call sites.
/// </summary>
public static class ClaudeCliLocator
{
    public static string ExecutableName => "claude";

    public static bool IsInstalled => ExecutableLocator.Exists(ExecutableName);
}
