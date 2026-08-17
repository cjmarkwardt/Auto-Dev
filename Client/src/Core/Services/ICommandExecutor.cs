namespace AutoDev.Core.Services;

/// <summary>Runs an arbitrary shell command line rooted at a working directory - backs the Command tab's simple REPL-style console.</summary>
public interface ICommandExecutor
{
    Task<int> RunAsync(
        string workingDirectory,
        string commandLine,
        Action<string> onStdOut,
        Action<string> onStdErr,
        CancellationToken cancellationToken = default);
}
