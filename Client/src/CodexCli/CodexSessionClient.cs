using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AutoDev.AiCli;
using AutoDev.AiCli.Models;
using Microsoft.Extensions.Logging;

namespace AutoDev.CodexCli;

/// <summary>
/// Wraps Codex's own turn model: unlike `claude -p`'s one long-lived process fed multiple turns over
/// stdin, `codex exec` (and `codex exec resume &lt;thread_id&gt;`) is one process per turn, taking that
/// turn's prompt as a CLI argument and exiting once it's done. Start() just records the resume id (if
/// any); each SendUserMessageAsync call spawns its own process against the current thread id, captures the
/// real thread id from that process's own "thread.started" event once the very first turn runs, and reuses
/// it via `exec resume` for every turn after. Turns are serialized through TurnLock rather than allowed to
/// overlap - Claude's equivalent (writing another stdin line while a prior one is still being answered) has
/// no real analogue here, since there's no live process to interject into until the current one exits.
/// </summary>
public sealed class CodexSessionClient : IAiSessionClient
{
    private readonly string _workspacePath;
    private readonly string _model;
    private readonly string? _effort;
    private readonly ILogger _logger;
    private readonly Channel<AiStreamEvent> _channel = Channel.CreateUnbounded<AiStreamEvent>();
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    private string? _threadId;
    private Process? _currentProcess;
    private bool _started;
    private bool _disposed;
    private CancellationTokenSource? _lifetimeCts;

    private long _cumulativeInputTokens;
    private long _cumulativeOutputTokens;
    private long _cumulativeCachedInputTokens;
    private long _cumulativeCacheWriteInputTokens;

    public CodexSessionClient(string workspacePath, string model, string? effort, ILogger logger)
    {
        _workspacePath = workspacePath;
        _model = model;
        _effort = effort;
        _logger = logger;
    }

    public string SessionId { get; private set; } = "";

    public bool IsRunning => _currentProcess is { HasExited: false };

    public void Start(string? resumeSessionId = null)
    {
        _lifetimeCts = new CancellationTokenSource();
        _threadId = string.IsNullOrEmpty(resumeSessionId) ? null : resumeSessionId;
        SessionId = _threadId ?? "";
        _started = true;
    }

    public IAsyncEnumerable<AiStreamEvent> ReadAllEventsAsync(CancellationToken cancellationToken = default) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public Task SendUserMessageAsync(string text, CancellationToken cancellationToken = default) =>
        RunTurnAsync(text, [], cancellationToken);

    public Task SendUserMessageAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken = default) =>
        RunTurnAsync(text, images, cancellationToken);

    private async Task RunTurnAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        if (!_started || _lifetimeCts is null)
        {
            throw new InvalidOperationException("Session has not been started.");
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);

        await _turnLock.WaitAsync(linkedCts.Token);
        try
        {
            if (_disposed)
            {
                return;
            }

            await RunProcessTurnAsync(text, images, linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Disposed/cancelled while queued behind a prior turn - nothing to run.
        }
        finally
        {
            _turnLock.Release();
        }
    }

    private async Task RunProcessTurnAsync(string text, IReadOnlyList<ImageAttachment> images, CancellationToken cancellationToken)
    {
        var tempImagePaths = await WriteTempImagesAsync(images);
        Process process;
        try
        {
            process = StartProcess(text, tempImagePaths);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start codex process");
            await EmitAsync(FailureResult($"Failed to start codex: {ex.Message}"), cancellationToken);
            CleanUpTempImages(tempImagePaths);
            return;
        }

        _currentProcess = process;
        _ = Task.Run(() => DrainStderrAsync(process, cancellationToken), CancellationToken.None);

        var turnText = new StringBuilder();
        var sawTerminalEvent = false;

        try
        {
            while (await ReadLineAsync(process, cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (await HandleLineAsync(line, turnText, cancellationToken) is { } terminal)
                {
                    sawTerminalEvent = true;
                    await EmitAsync(terminal, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed mid-turn - process is killed in the finally block below.
        }

        try
        {
            using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(exitCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }

        // The process ended without ever reaching "turn.completed"/"turn.failed" (crashed, killed, an
        // unparseable stream) - unlike ClaudeSessionClient, whose death ends the whole channel (caught by
        // GenerateTabViewModel.ReadLoopAsync's own "stream ended, finalize abandoned turn" tail), this
        // client's channel stays open across turns, so nothing else would ever close out this specific
        // turn's active request - it would sit "Working" forever.
        if (!sawTerminalEvent && !cancellationToken.IsCancellationRequested)
        {
            await EmitAsync(FailureResult($"Codex process exited unexpectedly (exit code {SafeExitCode(process)})."), CancellationToken.None);
        }

        _currentProcess = null;
        process.Dispose();
        CleanUpTempImages(tempImagePaths);
    }

    /// <summary>Returns the turn-terminal ResultEvent if this line was "turn.completed"/"turn.failed", else null (having already emitted/accumulated whatever this line otherwise contributes).</summary>
    private async Task<ResultEvent?> HandleLineAsync(string line, StringBuilder turnText, CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var type = root.GetProperty("type").GetString() ?? "";

        switch (type)
        {
            case "turn.completed":
                AccumulateUsage(root);
                return SuccessResult(turnText);
            case "turn.failed":
                var message = root.TryGetProperty("error", out var errorEl) && errorEl.TryGetProperty("message", out var msgEl)
                    ? msgEl.GetString() ?? "Codex turn failed."
                    : "Codex turn failed.";
                return FailureResult(message);
        }

        var evt = CodexStreamEventParser.Parse(type, root);
        if (evt is null)
        {
            return null;
        }

        if (evt.SessionId is { Length: > 0 })
        {
            _threadId = evt.SessionId;
            SessionId = evt.SessionId;
        }

        if (evt is AssistantMessageEvent assistant)
        {
            turnText.Append(assistant.PlainText);
        }

        await EmitAsync(evt, cancellationToken);
        return null;
    }

    private void AccumulateUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage))
        {
            return;
        }

        _cumulativeInputTokens += GetLong(usage, "input_tokens");
        _cumulativeOutputTokens += GetLong(usage, "output_tokens") + GetLong(usage, "reasoning_output_tokens");
        _cumulativeCachedInputTokens += GetLong(usage, "cached_input_tokens");
        _cumulativeCacheWriteInputTokens += GetLong(usage, "cache_write_input_tokens");
    }

    private static long GetLong(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0L;

    private ResultEvent SuccessResult(StringBuilder turnText) => new()
    {
        Type = "result",
        IsError = false,
        Result = turnText.Length > 0 ? turnText.ToString() : null,
        ModelUsage = CumulativeModelUsage(),
    };

    private ResultEvent FailureResult(string message) => new()
    {
        Type = "result",
        IsError = true,
        Result = message,
        ModelUsage = CumulativeModelUsage(),
    };

    private Dictionary<string, ModelUsageEntry> CumulativeModelUsage() => new()
    {
        [_model] = new ModelUsageEntry(
            _cumulativeInputTokens,
            _cumulativeOutputTokens,
            _cumulativeCachedInputTokens,
            _cumulativeCacheWriteInputTokens,
            0m),
    };

    private Process StartProcess(string text, IReadOnlyList<string> tempImagePaths)
    {
        var startInfo = new ProcessStartInfo(CodexCliLocator.ExecutableName)
        {
            WorkingDirectory = _workspacePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("exec");

        if (_threadId is { Length: > 0 })
        {
            startInfo.ArgumentList.Add("resume");
            startInfo.ArgumentList.Add(_threadId);
        }

        startInfo.ArgumentList.Add("--json");
        startInfo.ArgumentList.Add("--skip-git-repo-check");
        startInfo.ArgumentList.Add("--dangerously-bypass-approvals-and-sandbox");

        // `exec resume` has no -C flag of its own - WorkingDirectory above covers it for every turn, but the
        // very first turn (plain `exec`, no thread yet) needs it passed explicitly too.
        if (_threadId is null)
        {
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(_workspacePath);
        }

        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add(_model);

        if (!string.IsNullOrEmpty(_effort))
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add($"model_reasoning_effort={TomlString.Quote(_effort)}");
        }

        // Passed on every turn (not just the first) rather than relying on it staying in a resumed thread's
        // own context - harmless to repeat, and guarantees the guidance is never lost to context compaction.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"developer_instructions={TomlString.Quote(AiSystemPromptGuidance.Text)}");

        foreach (var path in tempImagePaths)
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(path);
        }

        startInfo.ArgumentList.Add(text);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start codex process.");

        // No stdin content to send - the prompt is already a CLI argument above. Left open, `codex exec`
        // waits to see whether stdin has anything piped into it before proceeding (see its own "Reading
        // additional input from stdin..." stderr line) - closing immediately gives it an instant EOF instead.
        process.StandardInput.Close();

        return process;
    }

    private static Task<string?> ReadLineAsync(Process process, CancellationToken cancellationToken) =>
        process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

    /// <summary>See ClaudeSessionClient.ReadStderrLoopAsync's own doc comment - an undrained stderr pipe can block the child's own writes once its buffer fills, so this must never stop draining just because one read/log call failed.</summary>
    private async Task DrainStderrAsync(Process process, CancellationToken cancellationToken)
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
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reading codex stderr - still draining");
                continue;
            }

            if (line is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(line))
            {
                _logger.LogDebug("codex stderr: {Line}", line);
            }
        }
    }

    private async Task EmitAsync(AiStreamEvent evt, CancellationToken cancellationToken)
    {
        try
        {
            await _channel.Writer.WriteAsync(evt, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Disposed - the channel may already be completing; dropping this trailing event is fine.
        }
    }

    private static async Task<List<string>> WriteTempImagesAsync(IReadOnlyList<ImageAttachment> images)
    {
        var paths = new List<string>();
        foreach (var image in images)
        {
            var extension = image.MediaType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "image/webp" => ".webp",
                _ => ".png",
            };

            var path = Path.Combine(Path.GetTempPath(), $"autodev-codex-{Guid.NewGuid():N}{extension}");
            await File.WriteAllBytesAsync(path, Convert.FromBase64String(image.Base64Data));
            paths.Add(path);
        }

        return paths;
    }

    private void CleanUpTempImages(IReadOnlyList<string> paths)
    {
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Failed to delete temporary Codex image attachment {Path}", path);
            }
        }
    }

    private static int SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _lifetimeCts?.Cancel();

        if (_currentProcess is { HasExited: false } process)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
        }

        // Lets any turn currently mid-RunProcessTurnAsync (or queued behind the lock) unwind before this
        // client is considered fully stopped - mirrors ClaudeSessionClient awaiting its process's own exit.
        try
        {
            using var waitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _turnLock.WaitAsync(waitCts.Token);
            _turnLock.Release();
        }
        catch (OperationCanceledException)
        {
            // Best effort - proceed with teardown regardless.
        }

        _lifetimeCts?.Dispose();
        _turnLock.Dispose();
        _channel.Writer.TryComplete();
    }
}

/// <summary>Codex's own concrete session-client factory - consumed only by AiSessionClientFactory, which dispatches IAiSessionClientFactory.Create's provider parameter to this (or ClaudeSessionClientFactory).</summary>
public sealed class CodexSessionClientFactory(ILoggerFactory loggerFactory)
{
    public IAiSessionClient Create(string workspacePath, string model, string? effort) =>
        new CodexSessionClient(workspacePath, model, effort, loggerFactory.CreateLogger<CodexSessionClient>());
}
