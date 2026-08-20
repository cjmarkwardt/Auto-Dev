namespace AutoDev.AiCli;

/// <summary>Checks whether a named executable resolves on PATH - shared by ClaudeCliLocator/CodexCliLocator's own IsInstalled, so the AuthGate can tell "not installed" apart from "installed but not signed in" without actually trying to launch either CLI.</summary>
public static class ExecutableLocator
{
    public static bool Exists(string executableName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
        {
            return false;
        }

        string[] candidateNames = OperatingSystem.IsWindows()
            ? [$"{executableName}.exe", $"{executableName}.cmd", $"{executableName}.bat"]
            : [executableName];

        foreach (var directory in pathVariable.Split(Path.PathSeparator))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            foreach (var candidateName in candidateNames)
            {
                if (File.Exists(Path.Combine(directory, candidateName)))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
