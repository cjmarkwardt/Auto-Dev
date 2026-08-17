using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace AutoDev.Views.Content;

/// <summary>
/// Registers an AvaloniaEdit highlighting definition for .task files (see TaskFileParser for the language
/// itself). There's no bundled TextMate grammar for a brand-new custom DSL like this, so this uses
/// AvaloniaEdit's own classic XSHD-based highlighting engine directly instead (the same engine behind its
/// built-in C#/Python/etc. definitions - see MarkdownCodeHighlightTheme's doc comment for more on this
/// engine), registered once under the name "Task" and looked up by extension the same way any built-in
/// language would be. A VS Code Dark+-ish palette: keywords blue, %VAR% references purple, quoted strings
/// and comments their usual colors, and the boolean-ish flags (overwrite/conditional/copy) a distinct teal
/// so they read as modifiers rather than instructions.
///
/// Commands whose whole rest-of-line is opaque data rather than further Task DSL syntax (var/script/run/
/// print/wait/delete/purge/cd/output) are matched as their own colorless-body Span instead of via the
/// generic Keywords list below - only the command word itself gets the Keyword color, everything after it
/// on the line renders as plain/default text with no further tokenization. Without this, e.g. "run dotnet
/// run" would highlight the SECOND "run" too (it's just a substring match against the same Keywords list),
/// and a shell command's own words could accidentally collide with a Task keyword and get miscolored -
/// both misleading, since that text isn't Task DSL at all. move/rename/file/folder are deliberately left
/// out of this treatment and still highlighted via the generic Keywords list below - their arguments
/// (targets, "->", overwrite/conditional/copy flags) genuinely are structured Task DSL tokens worth
/// coloring, unlike the other commands' opaque data.
/// </summary>
internal static class TaskFileHighlighting
{
    private const string Name = "Task";
    private static bool _registered;

    /// <summary>Cheap and idempotent - safe to call from every EditTabView instance's constructor.</summary>
    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        using var reader = XmlReader.Create(new StringReader(XshdXml));
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        HighlightingManager.Instance.RegisterHighlighting(Name, [".task"], definition);
    }

    public static IHighlightingDefinition? GetDefinition() => HighlightingManager.Instance.GetDefinitionByExtension(".task");

    private const string XshdXml = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="Task" extensions=".task" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
            <Color name="Comment" foreground="#6A9955" exampleText="# comment" />
            <Color name="String" foreground="#CE9178" exampleText="&quot;quoted value&quot;" />
            <Color name="Keyword" foreground="#569CD6" exampleText="run" />
            <Color name="Variable" foreground="#C586C0" exampleText="%NAME%" />
            <Color name="Number" foreground="#B5CEA8" exampleText="1.5" />
            <Color name="Flag" foreground="#4EC9B0" exampleText="overwrite" />

            <RuleSet>
                <Span color="Comment">
                    <Begin>\#</Begin>
                </Span>

                <Span color="String">
                    <Begin>"</Begin>
                    <End>"</End>
                    <RuleSet>
                        <Span begin="\\" end="." />
                    </RuleSet>
                </Span>

                <!-- Opaque-rest-of-line commands - see this file's class doc comment. Only the command word
                     (matched by Begin) gets colored; everything after it on the line is left as plain/
                     default text (no nested RuleSet, so nothing further is tokenized) until end of line. -->
                <Span>
                    <Begin color="Keyword">\b(var|script|run|print|wait|delete|purge|cd|output)\b</Begin>
                </Span>

                <Rule color="Variable">
                    %[A-Za-z_][A-Za-z0-9_]*%
                </Rule>

                <Rule color="Number">
                    \b\d+(\.\d+)?\b
                </Rule>

                <Rule color="Keyword">
                    ->
                </Rule>

                <Keywords color="Keyword">
                    <Word>move</Word>
                    <Word>rename</Word>
                    <Word>file</Word>
                    <Word>folder</Word>
                </Keywords>

                <Keywords color="Flag">
                    <Word>overwrite</Word>
                    <Word>conditional</Word>
                    <Word>copy</Word>
                </Keywords>
            </RuleSet>
        </SyntaxDefinition>
        """;
}
