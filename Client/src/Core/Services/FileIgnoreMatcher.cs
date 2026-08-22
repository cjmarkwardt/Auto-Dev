using System.Text;
using System.Text.RegularExpressions;

namespace AutoDev.Core.Services;

/// <summary>
/// A parsed, ready-to-match set of .gitignore-syntax patterns - see FilesSectionViewModel's own
/// .fileignore handling. Immutable once built (Parse); construct a fresh instance whenever the
/// source lines change rather than mutating one in place. Implements the common subset of the
/// .gitignore pattern language: blank lines and #comments are skipped, a trailing `/` restricts a
/// pattern to directories only, a `/` at the start or in the middle anchors it to the root (matches
/// only there) rather than at any depth, `*`/`?` match within one path segment, `**` matches across
/// segment boundaries, and a leading `!` negates a pattern - patterns are evaluated in file order
/// and the *last* one to match wins (so a later `!` line can re-include something an earlier
/// pattern excluded), the same as real .gitignore. Not a byte-for-byte reimplementation of git's own
/// matcher (no character classes, no escaped special characters) - just close enough for the
/// patterns anyone actually writes by hand.
/// </summary>
public sealed class FileIgnoreMatcher
{
    private const string FileIgnoreFileName = ".fileignore";
    private const string GitIgnoreFileName = ".gitignore";

    /// <summary>A line in .fileignore consisting of exactly this (surrounding whitespace ignored) is replaced with .gitignore's own lines - see LoadForWorkspace/FilesSectionViewModel.ReloadFileIgnore, which uses the identical directive.</summary>
    private const string GitIgnoreDirective = "$gitignore";

    private readonly IReadOnlyList<Rule> rules;

    private FileIgnoreMatcher(IReadOnlyList<Rule> rules) => this.rules = rules;

    /// <summary>
    /// Reads `workspacePath`'s own .fileignore (if any) into a ready-to-query matcher, expanding a bare
    /// "$gitignore" line into .gitignore's own lines at that point - shared logic behind both
    /// FilesSectionViewModel's file tree (ReloadFileIgnore) and FileSearchViewModel's F1 quick-open (both
    /// file-name and content search), so a workspace-relative path either app-visible file listing skips
    /// stays in sync with the other. Null if no .fileignore exists at the workspace root at all, or it
    /// couldn't be read - callers fall back to whatever they'd otherwise use (typically plain .gitignore
    /// filtering) in that case. An empty or unreadable-but-present .fileignore is intentionally not
    /// distinguished from "absent" here (unlike FilesSectionViewModel's own incremental reload, which keeps
    /// a previous ruleset in place across a transient read failure instead of dropping it) - this is always a
    /// fresh, one-shot read with no prior state to preserve.
    /// </summary>
    public static FileIgnoreMatcher? LoadForWorkspace(string workspacePath)
    {
        var fileIgnorePath = Path.Combine(workspacePath, FileIgnoreFileName);
        if (!File.Exists(fileIgnorePath))
        {
            return null;
        }

        IReadOnlyList<string> lines;
        try
        {
            lines = File.ReadAllLines(fileIgnorePath);
        }
        catch
        {
            return null;
        }

        List<string> expanded = [];
        foreach (var line in lines)
        {
            if (line.Trim() != GitIgnoreDirective)
            {
                expanded.Add(line);
                continue;
            }

            var gitIgnorePath = Path.Combine(workspacePath, GitIgnoreFileName);
            if (!File.Exists(gitIgnorePath))
            {
                continue;
            }

            try
            {
                expanded.AddRange(File.ReadAllLines(gitIgnorePath));
            }
            catch
            {
                // best-effort - a transient read failure just skips the merge this time
            }
        }

        return Parse(expanded);
    }

    /// <summary>Parses `lines` (a raw .fileignore file's own lines, already with any `$gitignore` line expanded by the caller - see FilesSectionViewModel) into a ready-to-query matcher.</summary>
    public static FileIgnoreMatcher Parse(IEnumerable<string> lines)
    {
        List<Rule> rules = [];
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var isNegation = line.StartsWith('!');
            if (isNegation)
            {
                line = line[1..];
            }

            var directoryOnly = line.EndsWith('/');
            if (directoryOnly)
            {
                line = line[..^1];
            }

            if (line.Length == 0)
            {
                continue;
            }

            rules.Add(new Rule(BuildRegex(line), isNegation, directoryOnly));
        }

        return new FileIgnoreMatcher(rules);
    }

    /// <summary>Whether workspace-relative `relativePath` (forward or backward slashes, either works) should be hidden - true if it, or any of its ancestor directories, matches these rules. Checking every ancestor (not just the exact path) mirrors how a whole ignored directory hides everything underneath it in real .gitignore, without needing every descendant to separately match.</summary>
    public bool IsMatch(string relativePath, bool isDirectory)
    {
        var normalized = relativePath.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0)
        {
            return false;
        }

        var segments = normalized.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var prefix = string.Join('/', segments[..(i + 1)]);
            var prefixIsDirectory = i < segments.Length - 1 || isDirectory;
            if (EvaluateExact(prefix, prefixIsDirectory))
            {
                return true;
            }
        }

        return false;
    }

    private bool EvaluateExact(string path, bool isDirectory)
    {
        var matched = false;
        foreach (var rule in rules)
        {
            if (rule.DirectoryOnly && !isDirectory)
            {
                continue;
            }

            if (rule.Pattern.IsMatch(path))
            {
                matched = !rule.IsNegation;
            }
        }

        return matched;
    }

    private static Regex BuildRegex(string pattern)
    {
        var hasLeadingSlash = pattern.StartsWith('/');
        var body = pattern.TrimStart('/');
        var anchored = hasLeadingSlash || body.Contains('/');

        var translated = TranslateGlob(body);
        var full = anchored ? $"^{translated}$" : $"(^|.*/){translated}$";
        return new Regex(full, RegexOptions.Compiled);
    }

    private static string TranslateGlob(string body)
    {
        StringBuilder result = new();
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '*' && i + 1 < body.Length && body[i + 1] == '*')
            {
                result.Append(".*");
                i++;
                if (i + 1 < body.Length && body[i + 1] == '/')
                {
                    i++;
                }
            }
            else if (body[i] == '*')
            {
                result.Append("[^/]*");
            }
            else if (body[i] == '?')
            {
                result.Append("[^/]");
            }
            else
            {
                result.Append(Regex.Escape(body[i].ToString()));
            }
        }

        return result.ToString();
    }

    private sealed record Rule(Regex Pattern, bool IsNegation, bool DirectoryOnly);
}
