using System.Collections.Concurrent;
using System.Text;
using AutoDev.Core.Models;
using CliWrap;
using Microsoft.Extensions.Logging;

namespace AutoDev.Core.Services;

public sealed class WorkspaceScriptRunnerService(
    string workspacePath,
    IWorkspaceMetadataStore metadataStore,
    ILogger<WorkspaceScriptRunnerService> logger) : IWorkspaceScriptRunner
{
    private readonly ConcurrentDictionary<string, byte> activeRuns = new();
    private readonly ConcurrentDictionary<string, LiveScriptRun> liveRuns = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> runCancellations = new();
    private readonly ConcurrentDictionary<string, byte> userStopped = new();
    private CancellationTokenSource? cts;

    /// <summary>0 while idle, 1 while any .cs file in this workspace is running - guards RunNowAsync so only one script total ever runs at a time per workspace, regardless of which .cs file it is. Set/cleared with Interlocked rather than folded into _activeRuns.TryAdd itself, since that dictionary stays keyed by path (IsRunning(scriptId)/GetLiveRun still need to answer "is *this* script running") while this is a single, path-independent gate.</summary>
    private int runInProgress;

    public event Action<ScriptRef>? ScriptRunStarted;
    public event Action<ScriptRunRecord>? ScriptRunCompleted;

    public bool IsRunning(string scriptId) => activeRuns.ContainsKey(scriptId);

    public bool StopRun(string scriptId)
    {
        if (!runCancellations.TryGetValue(scriptId, out CancellationTokenSource? cts))
        {
            return false;
        }

        userStopped[scriptId] = 0;
        cts.Cancel();
        return true;
    }

    public LiveScriptRun? GetLiveRun(string scriptId) => liveRuns.GetValueOrDefault(scriptId);

    /// <summary>Scripts only ever run manually (see IWorkspaceScriptRunner) - the runner exists purely to track/broadcast the state of runs kicked off via RunNowAsync, so there's no background loop to start; kept only so the CancellationTokenSource every run links against exists before the first RunNowAsync call.</summary>
    public void Start() => cts ??= new CancellationTokenSource();

    public async Task RunNowAsync(ScriptRef script, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref runInProgress, 1, 0) != 0)
        {
            return; // another script (or this same one) is already running - only one script total runs at a time
        }

        activeRuns.TryAdd(script.Path, 0);
        try
        {
            await RunAndTrackAsync(script, cancellationToken);
        }
        finally
        {
            Interlocked.Exchange(ref runInProgress, 0);
        }
    }

    private async Task RunAndTrackAsync(ScriptRef script, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        // Link to the runner's own lifetime token so disposing it (workspace tab closed, app shutting down)
        // actually kills the underlying subprocess instead of leaving it orphaned and running forever with
        // no way to ever persist a run record for it. Not a `using` here - StopRun needs to reach this
        // specific run's token from outside, for as long as the run is active.
        CancellationTokenSource linkedCts = cts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCancellations[script.Path] = linkedCts;

        // GetLiveRun(script.Path) must already resolve by the time a ScriptRunStarted subscriber (see
        // ScriptTabViewModel.OnAnyRunStarted) reacts to it, so this is registered before that event fires
        // rather than after - a subscriber reacting to the event and immediately calling GetLiveRun would
        // otherwise race this method's own continuation.
        LiveScriptRun liveRun = new LiveScriptRun();
        liveRuns[script.Path] = liveRun;

        ScriptRunStarted?.Invoke(script);

        try
        {
            // No AutoDev-authored announcement or other commentary is ever appended to liveRun - it carries
            // only the process's own real stdout/stderr (plus, from SendInputAsync, the local echo of
            // whatever was typed back to it), so the Script tab reads exactly like a plain console window
            // observing this one process, start to finish.
            string fullPath = Path.Combine(workspacePath, script.Path);

            int exitCode = 0;
            try
            {
                // --file (rather than passing the path as a bare positional argument) works unambiguously
                // even when the workspace root already contains a project/solution file, which `dotnet run`
                // would otherwise try to run instead - see Docs/RunningScripts.md.
                Command command = Cli.Wrap("dotnet")
                    .WithArguments(["run", "--file", fullPath])
                    .WithWorkingDirectory(workspacePath)
                    .WithStandardInputPipe(PipeSource.Create(async (stream, pipeCancellationToken) =>
                    {
                        // Handed the process's own real stdin stream once it starts - captured here rather
                        // than written to directly, so LiveScriptRun.SendInputAsync can write to it at any
                        // later point the script actually needs input. CliWrap itself cancels
                        // pipeCancellationToken once the process exits, at which point there's nothing further
                        // to hand off - awaiting it (rather than returning immediately) is what keeps this
                        // pipe - and the process's own stdin - open for that entire span instead of closing it
                        // the instant this delegate would otherwise return.
                        liveRun.AttachStandardInput(stream);
                        try
                        {
                            await Task.Delay(Timeout.Infinite, pipeCancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }))
                    .WithStandardOutputPipe(PipeTarget.Create((stream, ct) => PumpAsync(stream, liveRun, ct)))
                    .WithStandardErrorPipe(PipeTarget.Create((stream, ct) => PumpAsync(stream, liveRun, ct)))
                    .WithValidation(CommandResultValidation.None);

                exitCode = (await command.ExecuteAsync(linkedCts.Token)).ExitCode;
            }
            catch (OperationCanceledException)
            {
                // Stopped mid-run (see StopRun) - the record below still gets built and persisted, marked
                // WasStopped, rather than propagating out and skipping that entirely.
            }

            bool wasStopped = userStopped.ContainsKey(script.Path);
            ScriptRunRecord record = new ScriptRunRecord
            {
                FilePath = script.Path,
                FileName = script.Name,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Success = !wasStopped && exitCode == 0,
                WasStopped = wasStopped,
                ExitCode = wasStopped ? null : exitCode,
                Output = liveRun.OutputText,
            };

            await PersistCompletedRunAsync(record, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Script run failed for {FilePath} in {WorkspacePath}", script.Path, workspacePath);
        }
        finally
        {
            liveRun.MarkFinished();
            runCancellations.TryRemove(script.Path, out _);
            userStopped.TryRemove(script.Path, out _);
            linkedCts.Dispose();
            liveRuns.TryRemove(script.Path, out _);
            activeRuns.TryRemove(script.Path, out _);
        }
    }

    private async Task PersistCompletedRunAsync(ScriptRunRecord record, CancellationToken cancellationToken)
    {
        await metadataStore.AppendScriptRunAsync(workspacePath, record, cancellationToken);
        ScriptRunCompleted?.Invoke(record);
    }

    /// <summary>
    /// Copies decoded text from a stdout/stderr stream into liveRun as raw chunks, exactly as they arrive -
    /// deliberately not CliWrap's own line-based ListenAsync/StandardOutputCommandEvent, which only surfaces
    /// text once a newline actually arrives. A script that prompts via `Console.Write("...: ")` (no trailing
    /// newline, so the read it's prompting for appears on the same line - see Markwardt.ScriptUtilities.
    /// Script.Query for a real example) would otherwise sit there invisibly waiting: the prompt itself stays
    /// buffered in CliWrap's own line reader until some later newline-terminated output happens to flush it
    /// out, at which point it appears merged together with whatever unrelated text triggered that flush.
    /// StreamReader (rather than decoding each raw byte chunk with Encoding.UTF8.GetString directly) is what
    /// correctly handles a multi-byte UTF-8 character landing across two separate reads.
    /// </summary>
    private static async Task PumpAsync(Stream stream, LiveScriptRun liveRun, CancellationToken cancellationToken)
    {
        using StreamReader reader = new StreamReader(stream, Encoding.UTF8);
        char[] buffer = new char[4096];
        int charsRead;
        while ((charsRead = await reader.ReadAsync(buffer, cancellationToken)) > 0)
        {
            liveRun.AppendText(new string(buffer, 0, charsRead));
        }
    }

    public void Dispose()
    {
        cts?.Cancel();
        cts?.Dispose();
    }
}

public sealed class ScriptRunnerServiceFactory(
    IWorkspaceMetadataStore metadataStore,
    ILoggerFactory loggerFactory) : IScriptRunnerServiceFactory
{
    public IWorkspaceScriptRunner Create(string workspacePath) => new WorkspaceScriptRunnerService(
        workspacePath,
        metadataStore,
        loggerFactory.CreateLogger<WorkspaceScriptRunnerService>());
}
