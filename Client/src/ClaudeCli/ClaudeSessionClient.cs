using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using AutoDev.AiCli;
using AutoDev.AiCli.Models;
using AutoDev.ClaudeCli.Serialization;
using Microsoft.Extensions.Logging;

namespace AutoDev.ClaudeCli;

public sealed class ClaudeSessionClient : IAiSessionClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new ClaudeStreamEventJsonConverter() },
    };

    private readonly string _workspacePath;
    private readonly string _model;
    private readonly string? _effort;
    private readonly ILogger _logger;
    private readonly Channel<AiStreamEvent> _channel = Channel.CreateUnbounded<AiStreamEvent>();

    private Process? _process;
    private CancellationTokenSource? _lifetimeCts;

    public ClaudeSessionClient(string workspacePath, string model, string? effort, ILogger logger)
    {
        _workspacePath = workspacePath;
        _model = model;
        _effort = effort;
        _logger = logger;
        SessionId = Guid.NewGuid().ToString();
    }

    public string SessionId { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    public void Start(string? resumeSessionId = null)
    {
        if (_process is not null)
        {
            throw new InvalidOperationException("Session already started.");
        }

        _lifetimeCts = new CancellationTokenSource();

        var startInfo = new ProcessStartInfo(ClaudeCliLocator.ExecutableName)
        {
            WorkingDirectory = _workspacePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add("--input-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("stream-json");
        startInfo.ArgumentList.Add("--verbose");
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(_model);

        if (!string.IsNullOrEmpty(_effort))
        {
            startInfo.ArgumentList.Add("--effort");
            startInfo.ArgumentList.Add(_effort);
        }

        startInfo.ArgumentList.Add("--permission-mode");
        startInfo.ArgumentList.Add("bypassPermissions");

        // AutoDev drives this whole session (Generate tab turns, and RunAutomatedTurnAsync's own
        // conflict-resolution turns) as exactly one Claude process per workspace session - disallowing the
        // Task tool keeps that true, since bypassPermissions would otherwise let Claude spawn its own
        // subagents as additional, AutoDev-invisible work happening outside this one process. TaskCreate/
        // TaskUpdate/TaskList/TaskGet/TaskStop/TaskOutput are the same underlying concern under newer, more
        // granular tool names (background/async task tracking) - left available, Claude would start a
        // background task and then poll TaskUpdate/TaskGet waiting on it, which the Generate tab's status box
        // has no way to distinguish from a genuine stall: the turn just sits showing "Using TaskUpdate"
        // indefinitely instead of ever finishing (each poll is a real event, so the stall watchdog never
        // fires either). Disallowing the whole family forces the same single-process, run-to-completion
        // behavior the plain "Task" disallow already establishes.
        startInfo.ArgumentList.Add("--disallowedTools");
        startInfo.ArgumentList.Add("Task");
        startInfo.ArgumentList.Add("TaskCreate");
        startInfo.ArgumentList.Add("TaskUpdate");
        startInfo.ArgumentList.Add("TaskList");
        startInfo.ArgumentList.Add("TaskGet");
        startInfo.ArgumentList.Add("TaskStop");
        startInfo.ArgumentList.Add("TaskOutput");

        startInfo.ArgumentList.Add("--append-system-prompt");
        startInfo.ArgumentList.Add(AiSystemPromptGuidance.Text);

        if (!string.IsNullOrEmpty(resumeSessionId))
        {
            SessionId = resumeSessionId;
            startInfo.ArgumentList.Add("--resume");
            startInfo.ArgumentList.Add(resumeSessionId);
        }
        else
        {
            startInfo.ArgumentList.Add("--session-id");
            startInfo.ArgumentList.Add(SessionId);
        }

        _process = Process.Start(startInfo)
                   ?? throw new InvalidOperationException("Failed to start claude process.");

        _ = Task.Run(() => ReadOutputLoopAsync(_process, _lifetimeCts.Token));
        _ = Task.Run(() => ReadStderrLoopAsync(_process, _lifetimeCts.Token));
    }

    private async Task ReadOutputLoopAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break; // stdout closed - process exited
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                AiStreamEvent evt;
                try
                {
                    evt = JsonSerializer.Deserialize<AiStreamEvent>(line, JsonOptions)
                          ?? throw new JsonException("Deserialized to null.");
                }
                catch (Exception ex)
                {
                    // Deliberately catches any exception, not just JsonException - the converter itself uses
                    // JsonElement.GetProperty (throws KeyNotFoundException, not JsonException) on fields it
                    // assumes are always present (e.g. "message" on an assistant/user event), so a stream-json
                    // line with an unexpected-but-still-valid-JSON shape could otherwise throw something this
                    // catch wouldn't see. Letting any single malformed/unexpected line escape here would kill
                    // this whole loop silently - stdout would never be read again for the rest of the process's
                    // life, so even a perfectly normal ResultEvent later in the stream would never be seen,
                    // leaving the Generate tab stuck showing "Working" forever with no way out but Cancel. One
                    // bad line should only ever cost that one line, never the rest of the session.
                    _logger.LogWarning(ex, "Failed to parse claude stream-json line: {Line}", line);
                    continue;
                }

                if (evt.SessionId is { Length: > 0 })
                {
                    SessionId = evt.SessionId;
                }

                await _channel.Writer.WriteAsync(evt, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on dispose
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Never lets anything other than cancellation stop this loop from draining stderr - unlike a lost stdout
    /// event (see ReadOutputLoopAsync's own per-line catch), a stderr line failing to read isn't just a
    /// missed log line: if this loop dies and nothing drains stderr for the rest of the process's life, the
    /// pipe eventually fills (a typical Linux pipe buffer is 64 KB) and the *child process's own write() to
    /// stderr blocks indefinitely* - it can stall mid-turn, unable to ever finish writing its stdout
    /// ResultEvent or exit, even though ReadOutputLoopAsync is working perfectly fine the whole time. This is
    /// exactly the shape of bug that left a Generate turn stuck reporting "Working" long after the assistant
    /// had visibly finished (its full reply already streamed and buffered) - the process wasn't sending
    /// anything more because it was blocked trying to flush a warning/log line to a stderr pipe nobody was
    /// reading anymore, likely because CreateNoWindow/a long, high-output turn made some earlier stderr line
    /// throw here without the older, narrower `catch (OperationCanceledException)` ever seeing it. A single
    /// bad read should only ever cost that one line, never draining stderr for the rest of the session.
    /// </summary>
    private async Task ReadStderrLoopAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await process.StandardError.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return; // expected on dispose
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading claude stderr - still draining");
                continue;
            }

            if (line is null)
            {
                return; // stderr closed - process exited
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                _logger.LogDebug("claude stderr: {Line}", line);
            }
        }
    }

    public IAsyncEnumerable<AiStreamEvent> ReadAllEventsAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default) =>
        WriteLineAsync(ClaudeInputMessageWriter.UserMessage(text), cancellationToken);

    public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken = default) =>
        WriteLineAsync(ClaudeInputMessageWriter.UserMessageWithAttachments(text, images), cancellationToken);

    private async Task WriteLineAsync(string json, CancellationToken cancellationToken)
    {
        if (_process is null)
        {
            throw new InvalidOperationException("Session has not been started.");
        }

        await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetimeCts?.Cancel();

        if (_process is { HasExited: false } process)
        {
            try
            {
                process.StandardInput.Close();
                using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(exitCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while shutting down claude session process");
            }
        }

        _process?.Dispose();
        _lifetimeCts?.Dispose();
        _channel.Writer.TryComplete();
    }
}

/// <summary>Claude's own concrete session-client factory - consumed only by AiSessionClientFactory, which dispatches IAiSessionClientFactory.Create's provider parameter to this (or CodexSessionClientFactory).</summary>
public sealed class ClaudeSessionClientFactory(ILoggerFactory loggerFactory)
{
    public IAiSessionClient Create(string workspacePath, string model, string? effort) =>
        new ClaudeSessionClient(workspacePath, model, effort, loggerFactory.CreateLogger<ClaudeSessionClient>());
}
