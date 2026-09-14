using Avalonia.Controls;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Content;

/// <summary>A simple REPL-style shell console for the workspace, rooted at the workspace's own directory by
/// default (see WorkingDirectory, changeable via GoHome or SetWorkingDirectory) - runs arbitrary commands via
/// ICommandExecutor, the same CliWrap-based subprocess pattern WorkspaceScriptRunnerService uses for `dotnet
/// run --file`.</summary>
public sealed partial class CommandTabViewModel(string workspacePath, ICommandExecutor executor, IUiDispatcher dispatcher) : ViewModelBase, IDisposable
{
    /// <summary>Copied out of the primary constructor's own workspacePath parameter once, here, so nothing else in this class reads that parameter directly - it's also used to initialize WorkingDirectory below, and reading the same primary-constructor parameter from more than one member trips CS9124 (ambiguous whether it's meant as shared constant state or a one-off initializer value).</summary>
    private readonly string workspaceRoot = workspacePath;

    private readonly List<string> history = [];
    private int historyIndex;
    private CancellationTokenSource? cts;

    [ObservableProperty]
    private string inputText = "";

    [ObservableProperty]
    private string outputText = "";

    [ObservableProperty]
    private bool isRunning;

    /// <summary>The resizable input row's height, bound two-way from CommandTabView.axaml's RowDefinition - persisted only in-memory for this tab's lifetime, same as GenerateTabViewModel.InputRowHeight.</summary>
    [ObservableProperty]
    private GridLength inputRowHeight = new(140);

    /// <summary>The directory commands actually run in (see RunAsync) - defaults to the workspace root, and only ever changes via GoHome or SetWorkingDirectory (the Files sidebar's "Set Command Context"), never implicitly by `cd`-ing inside a command line (each RunAsync call is a fresh CliWrap process with no shared shell state to persist that across commands).</summary>
    [ObservableProperty]
    private string workingDirectory = workspacePath;

    /// <summary>WorkingDirectory rendered relative to the workspace root, workspace-relative-path style ("/" at the root itself, "/sub/folder" otherwise) rather than an absolute filesystem path - see CommandTabView.axaml's path bar.</summary>
    public string WorkingDirectoryDisplay
    {
        get
        {
            string relative = Path.GetRelativePath(workspaceRoot, WorkingDirectory).Replace('\\', '/');
            return relative == "." ? "/" : $"/{relative}";
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    partial void OnWorkingDirectoryChanged(string value) => OnPropertyChanged(nameof(WorkingDirectoryDisplay));

    /// <summary>Called from FilesSectionViewModel's "Set Command Context" folder context menu item (wired in WorkspaceViewModel) - fullPath is always an existing directory already inside this workspace, so no validation beyond that is needed.</summary>
    public void SetWorkingDirectory(string fullPath) => WorkingDirectory = fullPath;

    [RelayCommand]
    private void GoHome() => WorkingDirectory = workspaceRoot;

    private bool CanRun() => !IsRunning && InputText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        string commandLine = InputText.Trim();
        if (commandLine.Length == 0)
        {
            return;
        }

        history.Add(commandLine);
        historyIndex = history.Count;

        AppendLine($"$ {commandLine}");
        InputText = "";
        IsRunning = true;
        cts = new CancellationTokenSource();

        try
        {
            int exitCode = await executor.RunAsync(WorkingDirectory, commandLine,
                line => dispatcher.Post(() => AppendLine(line)),
                line => dispatcher.Post(() => AppendLine(line)),
                cts.Token);
            AppendLine($"[exit code {exitCode}]");
        }
        catch (OperationCanceledException)
        {
            AppendLine("[stopped]");
        }
        finally
        {
            IsRunning = false;
            cts = null;
        }
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => cts?.Cancel();

    private void AppendLine(string line) => OutputText = OutputText.Length > 0 ? $"{OutputText}\n{line}" : line;

    /// <summary>Called from CommandTabView.axaml.cs on Up, only when the caret sits at offset 0 - lets normal multi-line cursor movement work everywhere else.</summary>
    public void RecallPrevious()
    {
        if (history.Count == 0)
        {
            return;
        }

        historyIndex = Math.Max(0, historyIndex - 1);
        InputText = history[historyIndex];
    }

    /// <summary>Called on Down, only when the caret sits at the end of the text.</summary>
    public void RecallNext()
    {
        if (historyIndex >= history.Count - 1)
        {
            historyIndex = history.Count;
            InputText = "";
            return;
        }

        historyIndex++;
        InputText = history[historyIndex];
    }

    public void Dispose() => cts?.Cancel();
}
