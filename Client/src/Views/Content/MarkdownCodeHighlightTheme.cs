using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Media;
using AvaloniaEdit.Highlighting;

namespace AutoDev.Views.Content;

/// <summary>
/// Recolors the highlighting definition behind a fenced ```csharp code block's embedded AvaloniaEdit
/// TextEditor (Markdown.Avalonia.SyntaxHigh's own "Re:C#"-named copy of AvaloniaEdit's built-in "C#" XSHD
/// definition - a freestanding object handed straight to that TextEditor.SyntaxHighlighting, never
/// registered in HighlightingManager.Instance, so it can only be reached per-instance through the editor
/// that already has it, not looked up globally) to a VS Code Dark+-like palette. The underlying engine is
/// the classic ICSharpCode XSHD highlighter - a different, far less capable engine than the TextMate/DarkPlus
/// grammar EditTabView's own code editor uses, with no semantic awareness of user-defined types - and its
/// bundled colors (Red, Blue, MidnightBlue, DarkBlue, Pink, Navy, ...) were picked for a light editor
/// background, either hard to read or actively misleading against this app's dark theme (e.g. built-in-type
/// keywords like "string" rendering bold red, "default"/"if"/etc. rendering pure blue, method names in
/// near-invisible MidnightBlue, numbers in near-invisible DarkBlue, and interpolated-string segments in
/// literal black).
/// </summary>
internal static class MarkdownCodeHighlightTheme
{
    // Matches an identifier that LOOKS like a type by C# convention (PascalCase) and isn't immediately
    // followed by "(" (the same lookahead the XSHD's own built-in MethodCall rule uses, kept mutually
    // exclusive with it on purpose - a call shouldn't also get recolored as a type). This is a heuristic,
    // not semantic analysis: this engine has no notion of what identifiers actually resolve to, so an
    // unconventionally-named local variable would false-positive. It's the only way to color a "known but
    // non-keyword" type (e.g. Task, CancellationToken - real .NET types with no dedicated XSHD keyword) or
    // an "unknown" (user-defined) type name at all with this engine - see this class's own doc comment for
    // why nothing closer to real semantic highlighting is available here. Deliberately does NOT also need to
    // exclude the XSHD's own built-in type keywords (bool/string/object/...) - those are all lowercase, so
    // this uppercase-first pattern never overlaps with them regardless.
    private static readonly Regex TypeNamePattern = new(@"\b[A-Z][A-Za-z0-9_]*\b(?!\s*\()");

    // Plain identifiers (variables, parameters, fields) - lowercase/underscore-first, not immediately
    // followed by "(" (same exclusion as TypeNamePattern, for the same reason: a call is a MethodCall, not a
    // plain identifier reference). Added after every other rule (see Apply), so by the same first-match-wins
    // rule order that keeps TypeNamePattern from stealing already-keyword-classified tokens, this only ever
    // catches identifiers no more specific rule already claimed - real keywords (also lowercase-first) keep
    // their own dedicated color untouched.
    private static readonly Regex IdentifierPattern = new(@"\b[a-z_][A-Za-z0-9_]*\b(?!\s*\()");

    /// <summary>Cheap and idempotent - safe (and expected) to call on every embedded code-block TextEditor found, every time a markdown view re-renders, rather than once globally.</summary>
    public static void Apply(IHighlightingDefinition definition)
    {
        const string keyword = "#569CD6";
        const string type = "#4EC9B0";
        const string identifier = "#9CDCFE";

        Recolor(definition, "Punctuation", "#D4D4D4");
        Recolor(definition, "Comment", "#6A9955");
        Recolor(definition, "String", "#CE9178");
        Recolor(definition, "StringInterpolation", "#D4D4D4");
        Recolor(definition, "Char", "#CE9178");
        Recolor(definition, "Preprocessor", "#9B9B9B");
        Recolor(definition, "MethodCall", "#DCDCAA");
        Recolor(definition, "NumberLiteral", "#B5CEA8");

        // Every remaining named color is some flavor of C# keyword (control-flow, modifiers, contextual
        // keywords, and - per the built-in-type-keywords-should-read-as-keywords-not-types request - the
        // XSHD's own built-in type-name keywords: bool/int/string/object/... plus the class/interface/
        // struct/enum/delegate declaration keywords bundled into those same two named-color groups). VS
        // Code's Dark+ theme colors essentially all of them the same keyword blue rather than distinguishing
        // each grammatical category the way this XSHD definition does, so a single shared color reads as far
        // more coherent than the original per-category palette.
        foreach (var name in new[]
                 {
                     "ValueTypeKeywords", "ReferenceTypeKeywords", "ThisOrBaseReference", "NullOrValueKeywords",
                     "Keywords", "GotoKeywords", "ContextKeywords", "ExceptionKeywords", "CheckedKeyword",
                     "UnsafeKeywords", "OperatorKeywords", "ParameterModifiers", "Modifiers", "Visibility",
                     "NamespaceKeywords", "GetSetAddRemove", "TrueFalse", "TypeKeywords", "SemanticKeywords",
                 })
        {
            Recolor(definition, name, keyword, normalWeight: true);
        }

        // Non-keyword types (known .NET types like Task/CancellationToken and unknown/user-defined ones
        // alike - see TypeNamePattern's own doc comment) and plain identifiers, in that order so the
        // (mutually-exclusive-by-case) identifier rule can't shadow the type rule for any shared position.
        AddRule(definition, TypeNamePattern, type);
        AddRule(definition, IdentifierPattern, identifier);
    }

    private static void Recolor(IHighlightingDefinition definition, string colorName, string hex, bool normalWeight = false)
    {
        var color = definition.GetNamedColor(colorName);
        if (color is null)
        {
            return;
        }

        color.Foreground = new SimpleHighlightingBrush(Color.Parse(hex));
        if (normalWeight)
        {
            // VS Code's Dark+ theme doesn't bold keywords - this XSHD definition does for most of them,
            // which reads as noticeably heavier than real VS Code output once the colors otherwise match.
            color.FontWeight = FontWeight.Normal;
        }
    }

    /// <summary>Guarded against duplicate insertion since Apply() runs on every markdown re-render and each embedded TextEditor's "Re:C#" is its own freestanding definition instance (never shared/cached), so without this check the same rule would pile up again on every single call against the same instance.</summary>
    private static void AddRule(IHighlightingDefinition definition, Regex pattern, string hex)
    {
        if (definition.MainRuleSet.Rules.Any(r => r.Regex?.ToString() == pattern.ToString()))
        {
            return;
        }

        definition.MainRuleSet.Rules.Add(new HighlightingRule
        {
            Regex = pattern,
            Color = new HighlightingColor { Foreground = new SimpleHighlightingBrush(Color.Parse(hex)) },
        });
    }
}
