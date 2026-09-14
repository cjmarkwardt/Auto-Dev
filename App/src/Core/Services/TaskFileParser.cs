namespace AutoDev.Core.Services;

/// <summary>
/// Parses a .task file's content into ordered batches of workspace-relative script paths - one non-empty line
/// per script, with a lone "-" line marking a wait: every script in the batches before it must finish
/// successfully before the batches after it start (see IWorkspaceScriptRunner.RunTaskNowAsync). Blank lines are
/// ignored; a marker with nothing before/after it (leading, trailing, or doubled up) simply produces no batch
/// there rather than an empty wait.
/// </summary>
public sealed class TaskFileParser
{
    public IReadOnlyList<IReadOnlyList<string>> ParseBatches(string content)
    {
        List<List<string>> batches = [[]];
        foreach (string rawLine in content.ReplaceLineEndings("\n").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line == "-")
            {
                batches.Add([]);
                continue;
            }

            batches[^1].Add(line);
        }

        return [.. batches.Where(batch => batch.Count > 0)];
    }
}
