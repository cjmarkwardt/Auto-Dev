using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

/// <summary>
/// Reads/writes the `.autodev/` folder inside a workspace: task run history and the Generate-tab session
/// link/draft. Everything under `.autodev/local/` (task run history, Generate session links and drafts) is
/// local-only bookkeeping, excluded from git via IWorkspaceVersioningService.EnsureLocalGitExcludeAsync
/// (.git/info/exclude) rather than .gitignore. Tasks themselves are now plain .task files living wherever
/// the user puts them in the workspace (see IFileTreeService) - this store only keeps their run history,
/// keyed by the file's own workspace-relative path (see TaskRunRecord.TaskPath) rather than an issued Id.
/// </summary>
public interface IWorkspaceMetadataStore
{
    void EnsureInitialized(string workspacePath);

    Task AppendTaskRunAsync(string workspacePath, TaskRunRecord record, CancellationToken cancellationToken = default);
    Task<List<TaskRunRecord>> LoadTaskRunsAsync(string workspacePath, string taskPath, CancellationToken cancellationToken = default);

    /// <summary>Every task with at least one run recorded, newest-run-first - lets the Output tab re-seed its dropdown on load without a central task registry to enumerate.</summary>
    Task<IReadOnlyList<TaskRef>> LoadRunTaskRefsAsync(string workspacePath, CancellationToken cancellationToken = default);

    /// <summary>Generate conversations are keyed by feature branch name - each feature gets its own independent session, resumed when you switch back to it.</summary>
    Task<string?> LoadGenerateSessionIdAsync(string workspacePath, string sessionKey, CancellationToken cancellationToken = default);
    Task SaveGenerateSessionIdAsync(string workspacePath, string sessionKey, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Not-yet-sent Generate input text, keyed the same way as the session itself - survives closing and reopening the workspace (or the whole app) without losing what was typed. Null/empty draft is cleared, not stored.</summary>
    Task<string?> LoadGenerateDraftAsync(string workspacePath, string sessionKey, CancellationToken cancellationToken = default);
    Task SaveGenerateDraftAsync(string workspacePath, string sessionKey, string draftText, CancellationToken cancellationToken = default);

    /// <summary>The last up-to-5 Generate requests for a session (see GenerateRequest), keyed the same way as the session itself - the Generate tab's entire displayed history, cyclable via prev/next. Newest last.</summary>
    Task<List<GenerateRequest>> LoadGenerateRequestsAsync(string workspacePath, string sessionKey, CancellationToken cancellationToken = default);
    Task SaveGenerateRequestsAsync(string workspacePath, string sessionKey, List<GenerateRequest> requests, CancellationToken cancellationToken = default);
}
