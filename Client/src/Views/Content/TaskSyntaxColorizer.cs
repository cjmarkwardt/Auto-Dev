using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Markwardt.TaskRunner;

namespace AutoDev.Views.Content;

/// <summary>
/// Colorizes .task source text in an AvaloniaEdit editor according to each line's structural role,
/// determined by re-parsing the document's indentation (via Markwardt.TaskRunner's own IndentationParser) on
/// every change. Coloring is purely positional: a word is only treated as a keyword, instruction name,
/// variable name, or script name when it actually occupies that position in the document's structure, never
/// merely because its text happens to match. Ported from Markwardt.TaskRunner's own Runner app
/// (https://github.com/cjmarkwardt/Task-Runner, Runner/TaskSyntaxColorizer.cs) rather than referenced
/// directly - it's part of that solution's desktop app, not the published Markwardt.TaskRunner NuGet
/// package, but is written entirely against that package's own public parsing types
/// (IndentationParser/LanguageKeywords/InstructionLabels/TaskParseException), so it needed no changes
/// beyond its own namespace to drop in here.
///
/// Attached to EditTabView's editor's TextView.LineTransformers only while a .task file is open (see
/// EditTabView.axaml.cs's UpdateLanguage) - ColorizeInsertions below colors any "{Name}" text unconditionally
/// regardless of structural line kind, which would miscolor unrelated file types if left attached to them.
/// </summary>
internal sealed class TaskSyntaxColorizer : DocumentColorizingTransformer
{
    private static readonly SolidColorBrush BlueBrush = new(Color.FromRgb(86, 156, 214));
    private static readonly SolidColorBrush OrangeBrush = new(Color.FromRgb(255, 165, 0));
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(96, 200, 96));
    private static readonly SolidColorBrush CommentBrush = new(Color.FromRgb(106, 153, 85));

    private readonly Dictionary<int, LineKind> lineKinds = [];

    private enum LineKind
    {
        Variable,
        Script,
        Instruction,
        Argument,
    }

    /// <summary>
    /// Re-parses the given document text and rebuilds the per-line structural classification used for
    /// coloring. Has no effect on lines while the document's indentation is transiently inconsistent
    /// (e.g. mid-edit); those lines render with no special coloring until it becomes consistent again.
    /// </summary>
    /// <param name="documentText">The current full text of the document.</param>
    public void UpdateStructure(string documentText)
    {
        lineKinds.Clear();

        try
        {
            ClassifyTopLevel(IndentationParser.Parse(documentText));
        }
        catch (TaskParseException)
        {
            lineKinds.Clear();
        }
    }

    /// <inheritdoc />
    protected override void ColorizeLine(DocumentLine line)
    {
        if (line.Length == 0)
        {
            return;
        }

        string text = CurrentContext.Document.GetText(line.Offset, line.Length);
        if (IsCommentLine(text))
        {
            ApplyColor(line, LeadingWhitespaceLength(text), text.Length, CommentBrush);
            return;
        }

        switch (lineKinds.GetValueOrDefault(line.LineNumber, LineKind.Argument))
        {
            case LineKind.Variable:
                ColorizeVariableLine(line, text);
                break;
            case LineKind.Script:
                ColorizeScriptLine(line, text);
                break;
            case LineKind.Instruction:
                ColorizeInstructionLine(line, text);
                break;
            default:
                break;
        }

        ColorizeInsertions(line, text);
    }

    private void ClassifyTopLevel(IReadOnlyList<IndentedLine> lines)
    {
        foreach (IndentedLine line in lines)
        {
            if (StartsWithKeyword(line.Text, LanguageKeywords.Script))
            {
                lineKinds[line.LineNumber] = LineKind.Script;
                ClassifyInstructions(line.Children);
            }
            else if (StartsWithKeyword(line.Text, LanguageKeywords.Variable))
            {
                lineKinds[line.LineNumber] = LineKind.Variable;
                ClassifyArguments(line.Children);
            }
            else
            {
                ClassifyArguments(line.Children);
            }
        }
    }

    private void ClassifyInstructions(IReadOnlyList<IndentedLine> lines)
    {
        foreach (IndentedLine line in lines)
        {
            lineKinds[line.LineNumber] = LineKind.Instruction;
            ClassifyArguments(line.Children);
        }
    }

    private void ClassifyArguments(IReadOnlyList<IndentedLine> lines)
    {
        foreach (IndentedLine line in lines)
        {
            lineKinds[line.LineNumber] = LineKind.Argument;
            ClassifyArguments(line.Children);
        }
    }

    private static bool StartsWithKeyword(string text, string keyword) =>
        text == keyword || text.StartsWith(keyword + " ", StringComparison.Ordinal);

    private void ColorizeVariableLine(DocumentLine line, string text)
    {
        int indent = LeadingWhitespaceLength(text);
        int keywordEnd = indent + LanguageKeywords.Variable.Length;
        if (!MatchesAt(text, indent, LanguageKeywords.Variable))
        {
            return;
        }

        ApplyColor(line, indent, keywordEnd, BlueBrush);

        int nameStart = keywordEnd;
        while (nameStart < text.Length && char.IsWhiteSpace(text[nameStart]))
        {
            nameStart++;
        }

        int equalsIndex = text.IndexOf('=', nameStart);
        if (equalsIndex < 0)
        {
            return;
        }

        int nameEnd = equalsIndex;
        while (nameEnd > nameStart && char.IsWhiteSpace(text[nameEnd - 1]))
        {
            nameEnd--;
        }

        if (nameEnd > nameStart)
        {
            ApplyColor(line, nameStart, nameEnd, OrangeBrush);
        }

        ApplyColor(line, equalsIndex, equalsIndex + 1, BlueBrush);
    }

    private void ColorizeScriptLine(DocumentLine line, string text)
    {
        int indent = LeadingWhitespaceLength(text);
        int keywordEnd = indent + LanguageKeywords.Script.Length;
        if (!MatchesAt(text, indent, LanguageKeywords.Script))
        {
            return;
        }

        ApplyColor(line, indent, keywordEnd, BlueBrush);

        int nameStart = keywordEnd;
        while (nameStart < text.Length && char.IsWhiteSpace(text[nameStart]))
        {
            nameStart++;
        }

        if (nameStart < text.Length)
        {
            ApplyColor(line, nameStart, text.Length, GreenBrush);
        }
    }

    private void ColorizeInstructionLine(DocumentLine line, string text)
    {
        int indent = LeadingWhitespaceLength(text);
        int labelEnd = indent;
        while (labelEnd < text.Length && !char.IsWhiteSpace(text[labelEnd]))
        {
            labelEnd++;
        }

        string label = text[indent..labelEnd];
        if (!InstructionLabels.All.Contains(label))
        {
            return;
        }

        ApplyColor(line, indent, labelEnd, BlueBrush);

        if (label != InstructionLabels.After)
        {
            return;
        }

        int argumentStart = labelEnd;
        while (argumentStart < text.Length && char.IsWhiteSpace(text[argumentStart]))
        {
            argumentStart++;
        }

        if (argumentStart < text.Length)
        {
            ApplyColor(line, argumentStart, text.Length, GreenBrush);
        }
    }

    private void ColorizeInsertions(DocumentLine line, string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{')
            {
                i++;
                continue;
            }

            if (i + 1 < text.Length && text[i + 1] == '{')
            {
                i += 2;
                continue;
            }

            int close = text.IndexOf('}', i + 1);
            if (close < 0)
            {
                break;
            }

            if (close > i + 1)
            {
                ApplyColor(line, i, close + 1, OrangeBrush);
            }

            i = close + 1;
        }
    }

    private void ApplyColor(DocumentLine line, int startInLine, int endInLine, SolidColorBrush brush)
    {
        if (endInLine <= startInLine)
        {
            return;
        }

        ChangeLinePart(line.Offset + startInLine, line.Offset + endInLine, element => element.TextRunProperties.SetForegroundBrush(brush));
    }

    private static int LeadingWhitespaceLength(string text)
    {
        int length = 0;
        while (length < text.Length && char.IsWhiteSpace(text[length]))
        {
            length++;
        }

        return length;
    }

    private static bool MatchesAt(string text, int start, string keyword) =>
        start + keyword.Length <= text.Length && text.AsSpan(start, keyword.Length).SequenceEqual(keyword);

    private static bool IsCommentLine(string text)
    {
        int indent = LeadingWhitespaceLength(text);
        return indent < text.Length && text[indent] == '#';
    }
}
