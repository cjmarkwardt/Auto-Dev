using Avalonia.Controls;
using AutoDev.Core.Services;
using AutoDev.ViewModels.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Content;

/// <summary>A simple REPL-style shell console for the workspace, rooted at the workspace's own directory by
/// default (see WorkingDirectory, changeable via GoHome or SetWorkingDirectory) - runs arbitrary commands via
/// the same ICommandExecutor/CliWrap backend the .task runner uses.</summary>
public sealed partial class CommandTabViewModel(string workspacePath, ICommandExecutor executor, IUiDispatcher dispatcher) : ViewModelBase, IDisposable
{
    /// <summary>Copied out of the primary constructor's own workspacePath parameter once, here, so nothing else in this class reads that parameter directly - it's also used to initialize WorkingDirectory below, and reading the same primary-constructor parameter from more than one member trips CS9124 (ambiguous whether it's meant as shared constant state or a one-off initializer value).</summary>
    private readonly string workspaceRoot = workspacePath;

    private readonly List<string> _history = [];
    private int _historyIndex;
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private string _outputText = "";

    [ObservableProperty]
    private bool _isRunning;

    /// <summary>The resizable input row's height, bound two-way from CommandTabView.axaml's RowDefinition - persisted only in-memory for this tab's lifetime, same as GenerateTabViewModel.InputRowHeight.</summary>
    [ObservableProperty]
    private GridLength _inputRowHeight = new(140);

    /// <summary>The directory commands actually run in (see RunAsync) - defaults to the workspace root, and only ever changes via GoHome or SetWorkingDirectory (the Files sidebar's "Set Command Context"), never implicitly by `cd`-ing inside a command line (each RunAsync call is a fresh CliWrap process with no shared shell state to persist that across commands).</summary>
    [ObservableProperty]
    private string _workingDirectory = workspacePath;

    /// <summary>WorkingDirectory rendered relative to the workspace root, workspace-relative-path style ("/" at the root itself, "/sub/folder" otherwise) rather than an absolute filesystem path - see CommandTabView.axaml's path bar.</summary>
    public string WorkingDirectoryDisplay
    {
        get
        {
            var relative = Path.GetRelativePath(workspaceRoot, WorkingDirectory).Replace('\\', '/');
            return relative == "." ? "/" : $"/{relative}";
        }
    }

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    partial void OnWorkingDirectoryChanged(string value) => OnPropertyChanged(nameof(WorkingDirectoryDisplay));

    /// <summary>Called from FilesSectionViewModel's "Set Command Context" folder context menu item (wired in WorkspaceTabViewModel) - fullPath is always an existing directory already inside this workspace, so no validation beyond that is needed.</summary>
    public void SetWorkingDirectory(string fullPath) => WorkingDirectory = fullPath;

    [RelayCommand]
    private void GoHome() => WorkingDirectory = workspaceRoot;

    private bool CanRun() => !IsRunning && InputText.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var commandLine = InputText.Trim();
        if (commandLine.Length == 0)
        {
            return;
        }

        _history.Add(commandLine);
        _historyIndex = _history.Count;

        AppendLine($"$ {commandLine}");
        InputText = "";
        IsRunning = true;
        _cts = new CancellationTokenSource();

        try
        {
            var exitCode = await executor.RunAsync(WorkingDirectory, commandLine,
                line => dispatcher.Post(() => AppendLine(line)),
                line => dispatcher.Post(() => AppendLine(line)),
                _cts.Token);
            AppendLine($"[exit code {exitCode}]");
        }
        catch (OperationCanceledException)
        {
            AppendLine("[stopped]");
        }
        finally
        {
            IsRunning = false;
            _cts = null;
        }
    }

    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => _cts?.Cancel();

    private void AppendLine(string line) => OutputText = OutputText.Length > 0 ? $"{OutputText}\n{line}" : line;

    /// <summary>Called from CommandTabView.axaml.cs on Up, only when the caret sits at offset 0 - lets normal multi-line cursor movement work everywhere else.</summary>
    public void RecallPrevious()
    {
        if (_history.Count == 0)
        {
            return;
        }

        _historyIndex = Math.Max(0, _historyIndex - 1);
        InputText = _history[_historyIndex];
    }

    /// <summary>Called on Down, only when the caret sits at the end of the text.</summary>
    public void RecallNext()
    {
        if (_historyIndex >= _history.Count - 1)
        {
            _historyIndex = _history.Count;
            InputText = "";
            return;
        }

        _historyIndex++;
        InputText = _history[_historyIndex];
    }

    public void Dispose() => _cts?.Cancel();
}
