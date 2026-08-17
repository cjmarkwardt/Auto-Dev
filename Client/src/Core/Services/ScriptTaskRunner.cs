using System.Text;
using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

public sealed class ScriptTaskRunner(ICommandExecutor executor) : IScriptTaskRunner
{
    public async Task<ScriptRunResult> RunAsync(
        string workspacePath,
        string scriptText,
        IProgress<ScriptOutputLine>? onLine = null,
        Action<ScriptBlockResult>? onBlockCompleted = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;

        TaskDocument document;
        try
        {
            document = TaskDocumentReader.ParseAndResolve(scriptText);
        }
        catch (FormatException ex)
        {
            return new ScriptRunResult(Success: false, ErrorMessage: ex.Message, Blocks: [], startedAt, DateTimeOffset.UtcNow);
        }

        if (document.Scripts.Count == 0)
        {
            return new ScriptRunResult(Success: false, ErrorMessage: "No scripts to run.", Blocks: [], startedAt, DateTimeOffset.UtcNow);
        }

        // Every script is its own independent long-lived process (a dev server, a client, etc.), not a
        // sequential step - they all start together and the run isn't "done" until every one of them has
        // finished, whether that's seconds (a one-shot script) or only once the user hits Stop.
        var results = await Task.WhenAll(document.Scripts.Select(script =>
            RunScriptAsync(workspacePath, script, onLine, onBlockCompleted, cancellationToken)));

        var success = results.All(r => r.Success);
        var errorMessage = success
            ? null
            : string.Join(' ', results.Where(r => !r.Success).Select(r => $"[{r.Name}] {r.ErrorMessage}"));

        return new ScriptRunResult(success, errorMessage, results, startedAt, DateTimeOffset.UtcNow);
    }

    /// <summary>Runs one script's commands sequentially (unlike the old bundled-shell-script approach, each command is now its own discrete step - a filesystem instruction executed directly in C#, or one Run instruction shelled out on its own). A command failure halts the rest of the script, matching the old per-block `set -e` semantics.</summary>
    private async Task<ScriptBlockResult> RunScriptAsync(
        string workspacePath,
        TaskScript script,
        IProgress<ScriptOutputLine>? onLine,
        Action<ScriptBlockResult>? onBlockCompleted,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var output = new StringBuilder();

        void Report(string line)
        {
            output.AppendLine(line);
            onLine?.Report(new ScriptOutputLine(script.Name, line));
        }

        string? errorMessage = null;
        var currentDirectory = workspacePath;
        foreach (var command in script.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Report(Describe(command));

            try
            {
                if (command.Instruction == ScriptInstruction.SetContext)
                {
                    currentDirectory = ResolveSetContext(currentDirectory, command);
                }
                else
                {
                    await ExecuteAsync(currentDirectory, command, Report, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errorMessage = $"{command.Instruction} failed: {ex.Message}";
                Report($"! {errorMessage}");
                break;
            }
        }

        var result = new ScriptBlockResult(
            script.Name,
            Success: errorMessage is null,
            ErrorMessage: errorMessage,
            Output: output.ToString(),
            startedAt,
            DateTimeOffset.UtcNow,
            script.Row,
            script.Column);

        onBlockCompleted?.Invoke(result);
        return result;
    }

    /// <summary>One line describing the instruction and its (already %VAR%-substituted) fields, reported before it runs - per the spec, this is what identifies each step in a script's output, followed by whatever the instruction itself reports (Run's stdout/stderr, or nothing for a silent success).</summary>
    private static string Describe(TaskCommand command) => command.Instruction switch
    {
        ScriptInstruction.Wait => $"$ Wait: Seconds={command.Seconds}",
        ScriptInstruction.Run => $"$ Run: Command={command.Command}",
        // Deliberately not "$ Print: Command=..." like every other instruction's own announcement line -
        // Print's whole point is to read like an echoed line of output, not a traced step, so this IS the
        // line reported (see the loop in RunScriptAsync, which reports Describe's return value verbatim).
        ScriptInstruction.Print => command.Command,
        ScriptInstruction.Move => $"$ Move: Target={command.Target}, Destination={command.Destination}, Copy={command.Copy}, Overwrite={command.Overwrite}",
        ScriptInstruction.Rename => $"$ Rename: Target={command.Target}, Name={command.Name}",
        ScriptInstruction.Create => $"$ Create: Target={command.Target}, Type={command.EntryKind}, Overwrite={command.Overwrite}, Conditional={command.Conditional}{DescribeContent(command)}",
        ScriptInstruction.Delete => $"$ Delete: Target={command.Target}",
        ScriptInstruction.Purge => $"$ Purge: Target={command.Target}",
        ScriptInstruction.SetContext => $"$ Set Context: Path={command.Path}",
        _ => $"$ {command.Instruction}",
    };

    private static string DescribeContent(TaskCommand command)
    {
        if (command.EntryKind != CreateEntryKind.File || command.Content.Length == 0)
        {
            return "";
        }

        // A file's Content can be arbitrarily long/multiline - this line is a human-readable step summary,
        // not the actual write (that happens in ExecuteCreate), so a huge value is sized instead of dumped.
        return command.Content.Length <= 40 ? $", Content={command.Content}" : $", Content=({command.Content.Length} chars)";
    }

    private Task ExecuteAsync(string currentDirectory, TaskCommand command, Action<string> report, CancellationToken cancellationToken) => command.Instruction switch
    {
        ScriptInstruction.Wait => Task.Delay(TimeSpan.FromSeconds(Math.Max(0, command.Seconds)), cancellationToken),
        ScriptInstruction.Run => ExecuteRunAsync(currentDirectory, command, report, cancellationToken),
        ScriptInstruction.Move => Task.Run(() => ExecuteMove(currentDirectory, command), cancellationToken),
        ScriptInstruction.Rename => Task.Run(() => ExecuteRename(currentDirectory, command), cancellationToken),
        ScriptInstruction.Create => Task.Run(() => ExecuteCreate(currentDirectory, command), cancellationToken),
        ScriptInstruction.Delete => Task.Run(() => ExecuteDelete(currentDirectory, command), cancellationToken),
        ScriptInstruction.Purge => Task.Run(() => ExecutePurge(currentDirectory, command), cancellationToken),
        _ => Task.CompletedTask,
    };

    private async Task ExecuteRunAsync(string currentDirectory, TaskCommand command, Action<string> report, CancellationToken cancellationToken)
    {
        var exitCode = await executor.RunAsync(currentDirectory, command.Command, report, report, cancellationToken);
        if (exitCode != 0)
        {
            throw new IOException($"`{command.Command}` exited with code {exitCode}.");
        }
    }

    /// <summary>"move bin/Program.exe build" -> new path build/Program.exe - Destination is always the folder Target's own basename gets placed inside (like `mv file dir/`), auto-created if it doesn't exist yet.</summary>
    private static void ExecuteMove(string currentDirectory, TaskCommand command)
    {
        var targetPath = ResolveRequired(currentDirectory, command.Target, "Target");
        var destinationDir = ResolveRequired(currentDirectory, command.Destination, "Destination");

        Directory.CreateDirectory(destinationDir);
        var baseName = Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var newPath = Path.Combine(destinationDir, baseName);

        if (PathExists(newPath))
        {
            if (!command.Overwrite)
            {
                throw new IOException($"'{newPath}' already exists.");
            }

            DeletePathRecursive(newPath);
        }

        if (command.Copy)
        {
            CopyRecursive(targetPath, newPath);
        }
        else if (Directory.Exists(targetPath))
        {
            Directory.Move(targetPath, newPath);
        }
        else
        {
            File.Move(targetPath, newPath);
        }
    }

    private static void ExecuteRename(string currentDirectory, TaskCommand command)
    {
        var targetPath = ResolveRequired(currentDirectory, command.Target, "Target");
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new ArgumentException("Name is required.");
        }

        if (!PathExists(targetPath))
        {
            throw new IOException($"'{targetPath}' does not exist.");
        }

        var parent = Path.GetDirectoryName(targetPath) ?? currentDirectory;
        var newPath = Path.Combine(parent, command.Name);
        if (PathExists(newPath))
        {
            throw new IOException($"'{newPath}' already exists.");
        }

        if (Directory.Exists(targetPath))
        {
            Directory.Move(targetPath, newPath);
        }
        else
        {
            File.Move(targetPath, newPath);
        }
    }

    private static void ExecuteCreate(string currentDirectory, TaskCommand command)
    {
        var targetPath = ResolveRequired(currentDirectory, command.Target, "Target");

        if (PathExists(targetPath))
        {
            if (command.Conditional)
            {
                return;
            }

            if (!command.Overwrite)
            {
                throw new IOException($"'{targetPath}' already exists.");
            }

            DeletePathRecursive(targetPath);
        }

        var parent = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (command.EntryKind == CreateEntryKind.Folder)
        {
            Directory.CreateDirectory(targetPath);
        }
        else
        {
            File.WriteAllText(targetPath, command.Content);
        }
    }

    private static void ExecuteDelete(string currentDirectory, TaskCommand command) =>
        DeletePathRecursive(ResolveRequired(currentDirectory, command.Target, "Target"));

    private static void ExecutePurge(string currentDirectory, TaskCommand command)
    {
        var targetPath = ResolveRequired(currentDirectory, command.Target, "Target");
        if (!Directory.Exists(targetPath))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(targetPath))
        {
            File.Delete(file);
        }

        foreach (var directory in Directory.EnumerateDirectories(targetPath))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Like a shell "cd": resolves Path against wherever the script's current directory already is (not always the workspace root), and fails if the result isn't an existing directory - never creates one.</summary>
    private static string ResolveSetContext(string currentDirectory, TaskCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Path))
        {
            throw new ArgumentException("Path is required.");
        }

        var newDirectory = Path.GetFullPath(Path.Combine(currentDirectory, command.Path));
        if (!Directory.Exists(newDirectory))
        {
            throw new IOException($"'{newDirectory}' does not exist.");
        }

        return newDirectory;
    }

    /// <summary>Guards every filesystem instruction against an empty/whitespace path resolving to the current directory itself (Path.Combine(currentDirectory, "") == currentDirectory) - without this, an empty Target on Delete/Purge would silently act on that entire directory.</summary>
    private static string ResolveRequired(string currentDirectory, string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{fieldName} is required.");
        }

        return Path.Combine(currentDirectory, value);
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void DeletePathRecursive(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void CopyRecursive(string sourcePath, string destinationPath)
    {
        if (!Directory.Exists(sourcePath))
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return;
        }

        Directory.CreateDirectory(destinationPath);
        foreach (var file in Directory.EnumerateFiles(sourcePath))
        {
            File.Copy(file, Path.Combine(destinationPath, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourcePath))
        {
            CopyRecursive(directory, Path.Combine(destinationPath, Path.GetFileName(directory)));
        }
    }
}
