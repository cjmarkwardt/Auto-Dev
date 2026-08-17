using CliWrap;
using CliWrap.EventStream;

namespace AutoDev.Core.Services;

public sealed class CommandExecutor : ICommandExecutor
{
    public async Task<int> RunAsync(
        string workingDirectory,
        string commandLine,
        Action<string> onStdOut,
        Action<string> onStdErr,
        CancellationToken cancellationToken = default)
    {
        var (shell, shellArgPrefix) = OperatingSystem.IsWindows()
            ? ("cmd.exe", "/c")
            : ("/bin/sh", "-c");

        var command = Cli.Wrap(shell)
            .WithArguments([shellArgPrefix, commandLine])
            .WithWorkingDirectory(workingDirectory)
            .WithValidation(CommandResultValidation.None);

        var exitCode = 0;
        await foreach (var cmdEvent in command.ListenAsync(cancellationToken))
        {
            switch (cmdEvent)
            {
                case StandardOutputCommandEvent stdOut:
                    onStdOut(stdOut.Text);
                    break;
                case StandardErrorCommandEvent stdErr:
                    onStdErr(stdErr.Text);
                    break;
                case ExitedCommandEvent exited:
                    exitCode = exited.ExitCode;
                    break;
            }
        }

        return exitCode;
    }
}
