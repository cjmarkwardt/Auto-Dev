using System.Globalization;
using System.Text;
using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

/// <summary>
/// Parses a .task file's own line-oriented, indentation-sensitive scripting language into a TaskDocument -
/// the hand-writable replacement for what used to be the file's raw JSON. Produces an UNRESOLVED document
/// (%VAR% references left as raw text); TaskDocumentReader still does substitution/validation afterward
/// exactly as it did for the JSON path, so this class's only job is syntax.
///
/// Grammar:
///   Top level: "var NAME = &lt;rest of line&gt;" and "script &lt;rest of line&gt;" headers - a script's name is
///   the ENTIRE rest of its header line, trimmed, not a single token (so "script My Test" names it "My
///   Test" with no quoting needed). The name is optional - a bare "script" line (nothing after it) is
///   valid and gets an auto-generated name ("Script &lt;N&gt;", N = its 1-based position among the document's
///   scripts) so it still displays as something and never collides with another unnamed script. "#"
///   comments (after trimming leading whitespace) and blank lines are ignored everywhere except inside a
///   multi-line command body (see below).
///
///   A script's commands are every subsequent line indented DEEPER than its own "script" header line,
///   ending at the first line (skipping blanks/comments) indented back to the header's level or shallower -
///   there is no "end" keyword. The first command line found under a script sets that script's own body
///   indent level; every sibling command in it must match that indent exactly (comparison is by raw
///   leading-whitespace character count, not a fixed tab width).
///
///   Inside a script, one instruction per line:
///     run &lt;rest of line&gt;   | run                      (bare - a multi-line body follows, indented deeper)
///     print &lt;rest of line&gt;
///     wait &lt;seconds&gt;
///     move &lt;target&gt; -&gt; &lt;destination&gt; [copy] [overwrite]
///     rename &lt;target&gt; -&gt; &lt;newname&gt;
///     file &lt;target&gt; [overwrite] [conditional]            (content optionally follows, indented deeper)
///     folder &lt;target&gt; [overwrite] [conditional]
///     delete &lt;target&gt;
///     purge &lt;target&gt;
///     cd &lt;path&gt;
///     output &lt;column&gt; &lt;row&gt;                              (1-based - see below; not an executed command)
///
///   A bare "run" or a "file" command can be followed by a multi-line body: every subsequent line indented
///   deeper than the command itself, ending at the first line indented back to the command's own level or
///   shallower (same rule as a script body, one level down). The body is taken verbatim - comments and
///   blank lines inside it are literal content, not stripped - and dedented by its own common leading
///   indentation, so it can be indented to match the surrounding script without that indentation ending up
///   in the body text. %VAR% substitution still applies to it afterward, same as any other field (see
///   TaskDocumentReader).
///
///   "output" pins the script's own live output panel to a specific Output tab grid cell (1-based, so
///   "output 1 2" means column 1, row 2 - converted to the 0-based TaskScript.Row/Column this parser
///   produces). It's written as a script command for readability but isn't one: it never becomes a
///   TaskCommand or appears in Commands, it just sets the enclosing TaskScript's own Row/Column directly.
///   Can appear anywhere in the script (last occurrence wins) since it's resolved at parse time, before
///   anything runs.
///
///   Target/destination/path tokens are whitespace-split with "quoted" support for embedded spaces.
/// </summary>
public static class TaskFileParser
{
    /// <exception cref="FormatException">Any syntax error, with a line number.</exception>
    public static TaskDocument Parse(string text)
    {
        var lines = SplitLines(text);
        var doc = new TaskDocument();
        var i = 0;
        while (true)
        {
            i = SkipBlankAndComments(lines, i);
            if (i >= lines.Count)
            {
                break;
            }

            var (raw, lineNumber) = lines[i];
            var indent = IndentOf(raw);
            var trimmed = raw.Trim();
            var (keyword, rest) = SplitFirstWord(trimmed);
            switch (keyword)
            {
                case "var":
                    doc.Variables.Add(ParseVar(rest, lineNumber));
                    i++;
                    break;
                case "script":
                    var (script, next) = ParseScript(lines, i, indent, rest, doc.Scripts.Count + 1);
                    doc.Scripts.Add(script);
                    i = next;
                    break;
                default:
                    throw Error(lineNumber, $"Unexpected line - expected 'var' or 'script', got '{keyword}'.");
            }
        }

        return doc;
    }

    private static TaskVariable ParseVar(string rest, int lineNumber)
    {
        var eq = rest.IndexOf('=');
        if (eq < 0)
        {
            throw Error(lineNumber, "Expected '=' in var declaration (e.g. 'var NAME = value').");
        }

        var name = rest[..eq].Trim();
        if (!IsValidIdentifier(name))
        {
            throw Error(lineNumber, $"Invalid variable name '{name}'.");
        }

        return new TaskVariable { Name = name, Value = rest[(eq + 1)..].Trim() };
    }

    private static (TaskScript Script, int NextIndex) ParseScript(List<(string Raw, int LineNumber)> lines, int headerIndex, int headerIndent, string rest, int ordinal)
    {
        var name = rest.Trim();
        if (name.Length == 0)
        {
            name = $"Script {ordinal}";
        }

        var script = new TaskScript { Name = name };
        var i = headerIndex + 1;
        int? bodyIndent = null;
        while (true)
        {
            var peek = SkipBlankAndComments(lines, i);
            if (peek >= lines.Count)
            {
                i = peek;
                break;
            }

            var (raw, lineNumber) = lines[peek];
            var indent = IndentOf(raw);
            if (indent <= headerIndent)
            {
                i = peek;
                break;
            }

            bodyIndent ??= indent;
            if (indent != bodyIndent)
            {
                throw Error(lineNumber, $"Inconsistent indentation in script '{name}' (expected {bodyIndent} leading spaces, got {indent}).");
            }

            var trimmed = raw.Trim();
            var (keyword, commandRest) = SplitFirstWord(trimmed);
            if (keyword == "output")
            {
                (script.Column, script.Row) = ParseOutput(commandRest, lineNumber);
                i = peek + 1;
                continue;
            }

            var (command, next) = ParseCommand(lines, peek, indent, trimmed, lineNumber);
            script.Commands.Add(command);
            i = next;
        }

        return (script, i);
    }

    /// <summary>"output 1 2" - column 1, row 2, both 1-based - converted here to the 0-based grid coordinates TaskScript.Row/Column (and everything downstream of it - see ScriptBlockGridLayout) actually use.</summary>
    private static (int Column, int Row) ParseOutput(string rest, int lineNumber)
    {
        var tokens = Tokenize(rest, lineNumber);
        if (tokens.Count != 2
            || !int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var column)
            || !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var row))
        {
            throw Error(lineNumber, "Expected 'output <column> <row>' (1-based).");
        }

        if (column < 1 || row < 1)
        {
            throw Error(lineNumber, "'output' column and row must be 1 or greater.");
        }

        return (column - 1, row - 1);
    }

    private static (TaskCommand Command, int NextIndex) ParseCommand(List<(string Raw, int LineNumber)> lines, int index, int commandIndent, string trimmed, int lineNumber)
    {
        var (keyword, rest) = SplitFirstWord(trimmed);
        switch (keyword)
        {
            case "run":
                return ParseRun(lines, index, commandIndent, rest, lineNumber);
            case "print":
                return (new TaskCommand { Instruction = ScriptInstruction.Print, Command = rest.Trim() }, index + 1);
            case "wait":
                return (ParseWait(rest, lineNumber), index + 1);
            case "move":
                return (ParseMove(rest, lineNumber), index + 1);
            case "rename":
                return (ParseRename(rest, lineNumber), index + 1);
            case "file":
                return ParseFile(lines, index, commandIndent, rest, lineNumber);
            case "folder":
                return (ParseFolder(rest, lineNumber), index + 1);
            case "delete":
                return (ParseSingleTarget(rest, lineNumber, ScriptInstruction.Delete, "delete"), index + 1);
            case "purge":
                return (ParseSingleTarget(rest, lineNumber, ScriptInstruction.Purge, "purge"), index + 1);
            case "cd":
                return (ParseCd(rest, lineNumber), index + 1);
            default:
                throw Error(lineNumber, $"Unknown instruction '{keyword}'.");
        }
    }

    private static (TaskCommand Command, int NextIndex) ParseRun(List<(string Raw, int LineNumber)> lines, int index, int commandIndent, string rest, int lineNumber)
    {
        var trimmedRest = rest.Trim();
        if (trimmedRest.Length > 0)
        {
            return (new TaskCommand { Instruction = ScriptInstruction.Run, Command = trimmedRest }, index + 1);
        }

        var (body, next) = TryReadIndentedBlock(lines, index + 1, commandIndent);
        if (body.Length == 0)
        {
            throw Error(lineNumber, "Expected a command after 'run', or an indented block on the following lines.");
        }

        return (new TaskCommand { Instruction = ScriptInstruction.Run, Command = body }, next);
    }

    private static TaskCommand ParseWait(string rest, int lineNumber)
    {
        var tokens = Tokenize(rest, lineNumber);
        if (tokens.Count != 1 || !double.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            throw Error(lineNumber, "Expected 'wait <seconds>'.");
        }

        return new TaskCommand { Instruction = ScriptInstruction.Wait, Seconds = seconds };
    }

    private static TaskCommand ParseMove(string rest, int lineNumber)
    {
        var tokens = Tokenize(rest, lineNumber);
        if (tokens.Count < 3 || tokens[1] != "->")
        {
            throw Error(lineNumber, "Expected 'move <target> -> <destination> [copy] [overwrite]'.");
        }

        bool copy = false, overwrite = false;
        for (var t = 3; t < tokens.Count; t++)
        {
            switch (tokens[t])
            {
                case "copy":
                    copy = true;
                    break;
                case "overwrite":
                    overwrite = true;
                    break;
                default:
                    throw Error(lineNumber, $"Unexpected token '{tokens[t]}' in move command.");
            }
        }

        return new TaskCommand { Instruction = ScriptInstruction.Move, Target = tokens[0], Destination = tokens[2], Copy = copy, Overwrite = overwrite };
    }

    private static TaskCommand ParseRename(string rest, int lineNumber)
    {
        var tokens = Tokenize(rest, lineNumber);
        if (tokens.Count != 3 || tokens[1] != "->")
        {
            throw Error(lineNumber, "Expected 'rename <target> -> <newname>'.");
        }

        return new TaskCommand { Instruction = ScriptInstruction.Rename, Target = tokens[0], Name = tokens[2] };
    }

    private static (TaskCommand Command, int NextIndex) ParseFile(List<(string Raw, int LineNumber)> lines, int index, int commandIndent, string rest, int lineNumber)
    {
        var (target, overwrite, conditional) = ParseEntryFlags(rest, lineNumber, "file");
        var (content, next) = TryReadIndentedBlock(lines, index + 1, commandIndent);

        return (new TaskCommand
        {
            Instruction = ScriptInstruction.Create,
            EntryKind = CreateEntryKind.File,
            Target = target,
            Overwrite = overwrite,
            Conditional = conditional,
            Content = content,
        }, next);
    }

    private static TaskCommand ParseFolder(string rest, int lineNumber)
    {
        var (target, overwrite, conditional) = ParseEntryFlags(rest, lineNumber, "folder");
        return new TaskCommand { Instruction = ScriptInstruction.Create, EntryKind = CreateEntryKind.Folder, Target = target, Overwrite = overwrite, Conditional = conditional };
    }

    private static (string Target, bool Overwrite, bool Conditional) ParseEntryFlags(string rest, int lineNumber, string keyword)
    {
        var tokens = Tokenize(rest, lineNumber);
        string? target = null;
        bool overwrite = false, conditional = false;
        foreach (var token in tokens)
        {
            if (token == "overwrite")
            {
                overwrite = true;
            }
            else if (token == "conditional")
            {
                conditional = true;
            }
            else if (target is null)
            {
                target = token;
            }
            else
            {
                throw Error(lineNumber, $"Unexpected token '{token}' in {keyword} command.");
            }
        }

        if (target is null)
        {
            throw Error(lineNumber, $"Expected a target path for '{keyword}'.");
        }

        return (target, overwrite, conditional);
    }

    private static TaskCommand ParseSingleTarget(string rest, int lineNumber, ScriptInstruction instruction, string keyword)
    {
        var tokens = Tokenize(rest, lineNumber);
        if (tokens.Count != 1)
        {
            throw Error(lineNumber, $"Expected exactly one target path after '{keyword}'.");
        }

        return new TaskCommand { Instruction = instruction, Target = tokens[0] };
    }

    private static TaskCommand ParseCd(string rest, int lineNumber)
    {
        var tokens = Tokenize(rest, lineNumber);
        if (tokens.Count != 1)
        {
            throw Error(lineNumber, "Expected exactly one path after 'cd'.");
        }

        return new TaskCommand { Instruction = ScriptInstruction.SetContext, Path = tokens[0] };
    }

    /// <summary>
    /// Consumes every subsequent line indented deeper than parentIndent as a multi-line body (blank lines
    /// included verbatim, trailing blank lines trimmed), dedented by its own common leading indentation;
    /// returns ("", index) untouched if there's no such block (the next non-blank, non-shallow-comment line
    /// isn't indented deeper). A comment line AT OR ABOVE parentIndent before the block starts is treated as
    /// an ordinary structural comment and skipped, same as anywhere else - only once a line is actually
    /// indented deeper does "no comment stripping" (see class doc comment) kick in for everything from there
    /// until the block ends.
    /// </summary>
    private static (string Content, int NextIndex) TryReadIndentedBlock(List<(string Raw, int LineNumber)> lines, int startIndex, int parentIndent)
    {
        var start = SkipBlankAndShallowComments(lines, startIndex, parentIndent);
        if (start >= lines.Count || IndentOf(lines[start].Raw) <= parentIndent)
        {
            return ("", startIndex);
        }

        var collected = new List<string>();
        var i = start;
        while (i < lines.Count)
        {
            var raw = lines[i].Raw;
            if (raw.Trim().Length == 0)
            {
                collected.Add(raw);
                i++;
                continue;
            }

            if (IndentOf(raw) <= parentIndent)
            {
                break;
            }

            collected.Add(raw);
            i++;
        }

        while (collected.Count > 0 && collected[^1].Trim().Length == 0)
        {
            collected.RemoveAt(collected.Count - 1);
        }

        return (Dedent(collected), i);
    }

    private static string Dedent(List<string> rawLines)
    {
        var minIndent = int.MaxValue;
        foreach (var line in rawLines)
        {
            if (line.Trim().Length == 0)
            {
                continue;
            }

            var indent = IndentOf(line);
            if (indent < minIndent)
            {
                minIndent = indent;
            }
        }

        if (minIndent == int.MaxValue)
        {
            minIndent = 0;
        }

        return string.Join('\n', rawLines.Select(l => l.Length >= minIndent ? l[minIndent..] : l.TrimStart()));
    }

    /// <summary>Whitespace-splits into tokens, with "quoted strings" (backslash-escaping '"' and '\') supported for embedded spaces.</summary>
    private static List<string> Tokenize(string s, int lineNumber)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i]))
            {
                i++;
            }

            if (i >= s.Length)
            {
                break;
            }

            if (s[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length)
                    {
                        throw Error(lineNumber, "Unterminated quoted string.");
                    }

                    if (s[i] == '\\' && i + 1 < s.Length && s[i + 1] is '"' or '\\')
                    {
                        sb.Append(s[i + 1]);
                        i += 2;
                        continue;
                    }

                    if (s[i] == '"')
                    {
                        i++;
                        break;
                    }

                    sb.Append(s[i]);
                    i++;
                }

                tokens.Add(sb.ToString());
            }
            else
            {
                var start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]))
                {
                    i++;
                }

                tokens.Add(s[start..i]);
            }
        }

        return tokens;
    }

    private static (string Word, string Remainder) SplitFirstWord(string s)
    {
        var trimmed = s.TrimStart();
        var idx = 0;
        while (idx < trimmed.Length && !char.IsWhiteSpace(trimmed[idx]))
        {
            idx++;
        }

        var word = trimmed[..idx];
        var remainder = idx < trimmed.Length ? trimmed[idx..].TrimStart() : "";
        return (word, remainder);
    }

    private static bool IsValidIdentifier(string s)
    {
        if (s.Length == 0 || (!char.IsLetter(s[0]) && s[0] != '_'))
        {
            return false;
        }

        for (var i = 1; i < s.Length; i++)
        {
            if (!char.IsLetterOrDigit(s[i]) && s[i] != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static int IndentOf(string raw) => raw.Length - raw.TrimStart().Length;

    private static int SkipBlankAndComments(List<(string Raw, int LineNumber)> lines, int index)
    {
        while (index < lines.Count)
        {
            var trimmed = lines[index].Raw.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                index++;
                continue;
            }

            break;
        }

        return index;
    }

    /// <summary>Like SkipBlankAndComments, but a comment line only counts as skippable while it's still at or above parentIndent - once a line is indented deeper than that, it's the start of a body (see TryReadIndentedBlock), even if it happens to start with '#'.</summary>
    private static int SkipBlankAndShallowComments(List<(string Raw, int LineNumber)> lines, int index, int parentIndent)
    {
        while (index < lines.Count)
        {
            var raw = lines[index].Raw;
            var trimmed = raw.TrimStart();
            if (trimmed.Length == 0)
            {
                index++;
                continue;
            }

            if (trimmed.StartsWith('#') && IndentOf(raw) <= parentIndent)
            {
                index++;
                continue;
            }

            break;
        }

        return index;
    }

    private static List<(string Raw, int LineNumber)> SplitLines(string text)
    {
        var rawLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<(string, int)>(rawLines.Length);
        for (var i = 0; i < rawLines.Length; i++)
        {
            result.Add((rawLines[i], i + 1));
        }

        return result;
    }

    private static FormatException Error(int lineNumber, string message) => new($"Line {lineNumber}: {message}");
}
