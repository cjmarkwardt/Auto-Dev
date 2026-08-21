using AutoDev.AiCli;

namespace AutoDev.Core.Services;

/// <summary>Resolves the `git` executable name/path - see ClaudeCliLocator/CodexCliLocator for the same seam on the AI CLI side. Unlike those two, git has no sign-in state of its own to check - just whether it's on PATH at all.</summary>
public static class GitCliLocator
{
    public static string ExecutableName => "git";

    public static bool IsInstalled => ExecutableLocator.Exists(ExecutableName);
}
