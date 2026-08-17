using System.Text.Json;
using AutoDev.Core.Models;
using AutoDev.Core.Serialization;

namespace AutoDev.Core.Services;

public sealed class WorkspaceMetadataStore : IWorkspaceMetadataStore
{
    private const string MetadataDirName = ".autodev";
    private const string LocalDirName = "local";
    private const string GenerateSessionsFileName = "generate-sessions.json";
    private const string GenerateDraftsFileName = "generate-drafts.json";
    private const string GenerateRequestsFileName = "generate-requests.json";
    private const string TaskRunsDirName = "task-runs";

    public void EnsureInitialized(string workspacePath)
    {
        Directory.CreateDirectory(MetadataDir(workspacePath));
        Directory.CreateDirectory(LocalDir(workspacePath));
    }

    public async Task AppendTaskRunAsync(string workspacePath, TaskRunRecord record, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(LocalDir(workspacePath), TaskRunsDirName, SanitizeTaskFolder(record.TaskPath));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{record.Id}.json");
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, record, AppJson.Options, cancellationToken);
    }

    public async Task<List<TaskRunRecord>> LoadTaskRunsAsync(string workspacePath, string taskPath, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(LocalDir(workspacePath), TaskRunsDirName, SanitizeTaskFolder(taskPath));
        var records = await LoadRunRecordsInFolderAsync(dir, cancellationToken);
        // Filters by exact TaskPath match as a safeguard against a (practically impossible) sanitize collision
        // between two different paths landing in the same folder.
        return [.. records.Where(r => r.TaskPath == taskPath).OrderByDescending(r => r.StartedAt)];
    }

    public async Task<IReadOnlyList<TaskRef>> LoadRunTaskRefsAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        var root = Path.Combine(LocalDir(workspacePath), TaskRunsDirName);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var refs = new Dictionary<string, TaskRef>();
        foreach (var folder in Directory.EnumerateDirectories(root))
        {
            var records = await LoadRunRecordsInFolderAsync(folder, cancellationToken);
            var newest = records.OrderByDescending(r => r.StartedAt).FirstOrDefault();
            if (newest is not null)
            {
                refs[newest.TaskPath] = new TaskRef(newest.TaskPath, newest.TaskName);
            }
        }

        return [.. refs.Values];
    }

    private static async Task<List<TaskRunRecord>> LoadRunRecordsInFolderAsync(string dir, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var records = new List<TaskRunRecord>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                await using var stream = File.OpenRead(file);
                var record = await JsonSerializer.DeserializeAsync<TaskRunRecord>(stream, AppJson.Options, cancellationToken);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // skip corrupt run file
            }
        }

        return records;
    }

    /// <summary>Turns a .task file's workspace-relative path into a filesystem-safe directory name for its run-history folder - replaces path separators and anything outside [A-Za-z0-9._-] with '_'. Opaque but deterministic; the original path is still recorded inside each TaskRunRecord, so nothing depends on reversing this.</summary>
    private static string SanitizeTaskFolder(string taskPath)
    {
        var chars = taskPath.Select(c => c is '/' or '\\' ? '_' : (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));
        return new string(chars.ToArray());
    }

    public async Task<string?> LoadGenerateSessionIdAsync(string workspacePath, string sessionKey, CancellationToken cancellationToken = default)
    {
        var sessions = await LoadStringDictAsync(GenerateSessionsFile(workspacePath), cancellationToken);
        return sessions.TryGetValue(sessionKey, out var sessionId) ? sessionId : null;
    }

    public async Task SaveGenerateSessionIdAsync(string workspacePath, string sessionKey, string sessionId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized(workspacePath);
        var sessions = await LoadStringDictAsync(GenerateSessionsFile(workspacePath), cancellationToken);
        sessions[sessionKey] = sessionId;
        await SaveStringDictAsync(GenerateSessionsFile(workspacePath), sessions, cancellationToken);
    }

    public async Task<string?> LoadGenerateDraftAsync(string workspacePath, string sessionKey, CancellationToken cancellationToken = default)
    {
        var drafts = await LoadStringDictAsync(GenerateDraftsFile(workspacePath), cancellationToken);
        return drafts.TryGetValue(sessionKey, out var draft) ? draft : null;
    }

    public async Task SaveGenerateDraftAsync(string workspacePath, string sessionKey, string draftText, CancellationToken cancellationToken = default)
    {
        var drafts = await LoadStringDictAsync(GenerateDraftsFile(workspacePath), cancellationToken);
        var changed = string.IsNullOrEmpty(draftText) ? drafts.Remove(sessionKey) : UpdateAndReportChanged(drafts, sessionKey, draftText);
        if (!changed)
        {
            return;
        }

        EnsureInitialized(workspacePath);
        await SaveStringDictAsync(GenerateDraftsFile(workspacePath), drafts, cancellationToken);
    }

    public async Task<List<GenerateRequest>> LoadGenerateRequestsAsync(string workspacePath, string sessionKey, CancellationToken cancellationToken = default)
    {
        var all = await LoadGenerateRequestsDictAsync(workspacePath, cancellationToken);
        return all.TryGetValue(sessionKey, out var requests) ? requests : [];
    }

    public async Task SaveGenerateRequestsAsync(string workspacePath, string sessionKey, List<GenerateRequest> requests, CancellationToken cancellationToken = default)
    {
        EnsureInitialized(workspacePath);
        var all = await LoadGenerateRequestsDictAsync(workspacePath, cancellationToken);
        all[sessionKey] = requests;
        await using var stream = File.Create(GenerateRequestsFile(workspacePath));
        await JsonSerializer.SerializeAsync(stream, all, AppJson.Options, cancellationToken);
    }

    private static async Task<Dictionary<string, List<GenerateRequest>>> LoadGenerateRequestsDictAsync(string workspacePath, CancellationToken cancellationToken)
    {
        var path = GenerateRequestsFile(workspacePath);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, List<GenerateRequest>>>(stream, AppJson.Options, cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool UpdateAndReportChanged(Dictionary<string, string> dict, string key, string value)
    {
        if (dict.TryGetValue(key, out var existing) && existing == value)
        {
            return false;
        }

        dict[key] = value;
        return true;
    }

    private static async Task<Dictionary<string, string>> LoadStringDictAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, AppJson.Options, cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task SaveStringDictAsync(string path, Dictionary<string, string> dict, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, dict, AppJson.Options, cancellationToken);
    }

    private static string MetadataDir(string workspacePath) => Path.Combine(workspacePath, MetadataDirName);
    private static string LocalDir(string workspacePath) => Path.Combine(MetadataDir(workspacePath), LocalDirName);
    private static string GenerateSessionsFile(string workspacePath) => Path.Combine(LocalDir(workspacePath), GenerateSessionsFileName);
    private static string GenerateDraftsFile(string workspacePath) => Path.Combine(LocalDir(workspacePath), GenerateDraftsFileName);
    private static string GenerateRequestsFile(string workspacePath) => Path.Combine(LocalDir(workspacePath), GenerateRequestsFileName);
}
