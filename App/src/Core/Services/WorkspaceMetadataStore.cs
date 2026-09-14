using System.Text.Json;
using AutoDev.Core.Models;
using AutoDev.Core.Serialization;

namespace AutoDev.Core.Services;

public sealed class WorkspaceMetadataStore : IWorkspaceMetadataStore
{
    private static readonly string metadataDirName = ".autodev";
    private static readonly string localDirName = "local";
    private static readonly string generateSessionsFileName = "generate-sessions.json";
    private static readonly string generateDraftsFileName = "generate-drafts.json";
    private static readonly string generateRequestsFileName = "generate-requests.json";
    private static readonly string scriptRunsDirName = "script-runs";

    public void EnsureInitialized(string workspacePath)
    {
        Directory.CreateDirectory(MetadataDir(workspacePath));
        Directory.CreateDirectory(LocalDir(workspacePath));
    }

    public async Task AppendScriptRunAsync(string workspacePath, ScriptRunRecord record, CancellationToken cancellationToken = default)
    {
        string dir = Path.Combine(LocalDir(workspacePath), scriptRunsDirName, SanitizeScriptFolder(record.FilePath));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"{record.Id}.json");
        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, record, AppJson.Options, cancellationToken);
    }

    public async Task<List<ScriptRunRecord>> LoadScriptRunsAsync(string workspacePath, string scriptPath, CancellationToken cancellationToken = default)
    {
        string dir = Path.Combine(LocalDir(workspacePath), scriptRunsDirName, SanitizeScriptFolder(scriptPath));
        List<ScriptRunRecord> records = await LoadRunRecordsInFolderAsync(dir, cancellationToken);
        // Filters by exact FilePath match as a safeguard against a (practically impossible) sanitize collision
        // between two different paths landing in the same folder.
        return [.. records.Where(r => r.FilePath == scriptPath).OrderByDescending(r => r.StartedAt)];
    }

    public async Task<IReadOnlyList<ScriptRef>> LoadRunScriptRefsAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        string root = Path.Combine(LocalDir(workspacePath), scriptRunsDirName);
        if (!Directory.Exists(root))
        {
            return [];
        }

        Dictionary<string, ScriptRef> refs = new Dictionary<string, ScriptRef>();
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            List<ScriptRunRecord> records = await LoadRunRecordsInFolderAsync(folder, cancellationToken);
            ScriptRunRecord? newest = records.OrderByDescending(r => r.StartedAt).FirstOrDefault();
            if (newest is not null)
            {
                refs[newest.FilePath] = new ScriptRef(newest.FilePath, newest.FileName);
            }
        }

        return [.. refs.Values];
    }

    private static async Task<List<ScriptRunRecord>> LoadRunRecordsInFolderAsync(string dir, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }

        List<ScriptRunRecord> records = new List<ScriptRunRecord>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                await using FileStream stream = File.OpenRead(file);
                ScriptRunRecord? record = await JsonSerializer.DeserializeAsync<ScriptRunRecord>(stream, AppJson.Options, cancellationToken);
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

    /// <summary>Turns a .cs file's workspace-relative path into a filesystem-safe directory name for its run-history folder - replaces path separators and anything outside [A-Za-z0-9._-] with '_'. Opaque but deterministic; the original path is still recorded inside each ScriptRunRecord, so nothing depends on reversing this.</summary>
    private static string SanitizeScriptFolder(string scriptPath)
    {
        IEnumerable<char> chars = scriptPath.Select(c => c is '/' or '\\' ? '_' : (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));
        return new string(chars.ToArray());
    }

    public async Task<string?> LoadGenerateSessionIdAsync(string workspacePath, string sessionKey, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> sessions = await LoadStringDictAsync(GenerateSessionsFile(workspacePath), cancellationToken);
        return sessions.TryGetValue(sessionKey, out string? sessionId) ? sessionId : null;
    }

    public async Task SaveGenerateSessionIdAsync(string workspacePath, string sessionKey, string sessionId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized(workspacePath);
        Dictionary<string, string> sessions = await LoadStringDictAsync(GenerateSessionsFile(workspacePath), cancellationToken);
        sessions[sessionKey] = sessionId;
        await SaveStringDictAsync(GenerateSessionsFile(workspacePath), sessions, cancellationToken);
    }

    public async Task<string?> LoadGenerateDraftAsync(string workspacePath, string sessionKey, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> drafts = await LoadStringDictAsync(GenerateDraftsFile(workspacePath), cancellationToken);
        return drafts.TryGetValue(sessionKey, out string? draft) ? draft : null;
    }

    public async Task SaveGenerateDraftAsync(string workspacePath, string sessionKey, string draftText, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> drafts = await LoadStringDictAsync(GenerateDraftsFile(workspacePath), cancellationToken);
        bool changed = string.IsNullOrEmpty(draftText) ? drafts.Remove(sessionKey) : UpdateAndReportChanged(drafts, sessionKey, draftText);
        if (!changed)
        {
            return;
        }

        EnsureInitialized(workspacePath);
        await SaveStringDictAsync(GenerateDraftsFile(workspacePath), drafts, cancellationToken);
    }

    public async Task<List<GenerateRequest>> LoadGenerateRequestsAsync(string workspacePath, string sessionKey, CancellationToken cancellationToken = default)
    {
        Dictionary<string, List<GenerateRequest>> all = await LoadGenerateRequestsDictAsync(workspacePath, cancellationToken);
        return all.TryGetValue(sessionKey, out List<GenerateRequest>? requests) ? requests : [];
    }

    public async Task SaveGenerateRequestsAsync(string workspacePath, string sessionKey, List<GenerateRequest> requests, CancellationToken cancellationToken = default)
    {
        EnsureInitialized(workspacePath);
        Dictionary<string, List<GenerateRequest>> all = await LoadGenerateRequestsDictAsync(workspacePath, cancellationToken);
        all[sessionKey] = requests;
        await using FileStream stream = File.Create(GenerateRequestsFile(workspacePath));
        await JsonSerializer.SerializeAsync(stream, all, AppJson.Options, cancellationToken);
    }

    private static async Task<Dictionary<string, List<GenerateRequest>>> LoadGenerateRequestsDictAsync(string workspacePath, CancellationToken cancellationToken)
    {
        string path = GenerateRequestsFile(workspacePath);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, List<GenerateRequest>>>(stream, AppJson.Options, cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool UpdateAndReportChanged(Dictionary<string, string> dict, string key, string value)
    {
        if (dict.TryGetValue(key, out string? existing) && existing == value)
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
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, AppJson.Options, cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task SaveStringDictAsync(string path, Dictionary<string, string> dict, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, dict, AppJson.Options, cancellationToken);
    }

    private static string MetadataDir(string workspacePath) => Path.Combine(workspacePath, metadataDirName);
    private static string LocalDir(string workspacePath) => Path.Combine(MetadataDir(workspacePath), localDirName);
    private static string GenerateSessionsFile(string workspacePath) => Path.Combine(LocalDir(workspacePath), generateSessionsFileName);
    private static string GenerateDraftsFile(string workspacePath) => Path.Combine(LocalDir(workspacePath), generateDraftsFileName);
    private static string GenerateRequestsFile(string workspacePath) => Path.Combine(LocalDir(workspacePath), generateRequestsFileName);
}
