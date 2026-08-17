namespace AutoDev.Tests.Core.Services;

/// <summary>Covers TaskFileParser's grammar: variables, script headers/auto-naming, every instruction keyword, indentation-sensitive multi-line bodies, quoting, and the syntax-error cases.</summary>
public sealed class TaskFileParserTests
{
    /// <summary>"var NAME = value" trims both sides and keeps the rest of the line verbatim as the value.</summary>
    [Fact]
    public void Parse_VarDeclaration_SetsNameAndValue()
    {
        TaskDocument doc = TaskFileParser.Parse("var GREETING = Hello, world");

        TaskVariable variable = Assert.Single(doc.Variables);
        Assert.Equal("GREETING", variable.Name);
        Assert.Equal("Hello, world", variable.Value);
    }

    /// <summary>A var line with no '=' is a syntax error, reported with its 1-based line number.</summary>
    [Fact]
    public void Parse_VarWithoutEquals_Throws()
    {
        FormatException ex = Assert.Throws<FormatException>(() => TaskFileParser.Parse("var BROKEN"));
        Assert.Contains("Line 1", ex.Message);
    }

    /// <summary>Variable names follow C#-identifier-like rules (letter/underscore first, alphanumeric/underscore after).</summary>
    [Theory]
    [InlineData("1BAD = x")]
    [InlineData("BAD-NAME = x")]
    [InlineData(" = x")]
    public void Parse_VarWithInvalidName_Throws(string varLine) =>
        Assert.Throws<FormatException>(() => TaskFileParser.Parse($"var {varLine}"));

    /// <summary>A script's name is the entire rest of its header line, not a single token - no quoting needed for spaces.</summary>
    [Fact]
    public void Parse_ScriptWithName_UsesFullRestOfLine()
    {
        TaskDocument doc = TaskFileParser.Parse("script My Test Script\n    print hi");

        Assert.Equal("My Test Script", doc.Scripts[0].Name);
    }

    /// <summary>A bare "script" line with nothing after it gets an auto-generated name based on its 1-based position among the document's scripts.</summary>
    [Fact]
    public void Parse_BareScript_AutoGeneratesOrdinalName()
    {
        TaskDocument doc = TaskFileParser.Parse(
            """
            script
                print first

            script Named
                print second

            script
                print third
            """);

        Assert.Equal(["Script 1", "Named", "Script 3"], doc.Scripts.Select(s => s.Name));
    }

    /// <summary>Blank lines and "#"-comments (after trimming leading whitespace) are ignored between top-level declarations.</summary>
    [Fact]
    public void Parse_BlankLinesAndComments_AreIgnoredBetweenScripts()
    {
        TaskDocument doc = TaskFileParser.Parse(
            """
            # leading comment

            var A = 1

              # indented comment
            script One
                print %A%
            """);

        Assert.Equal("1", doc.Variables[0].Value);
        Assert.Equal("One", doc.Scripts[0].Name);
    }

    /// <summary>A top-level line that isn't "var" or "script" is a syntax error naming the offending keyword.</summary>
    [Fact]
    public void Parse_UnexpectedTopLevelKeyword_Throws()
    {
        FormatException ex = Assert.Throws<FormatException>(() => TaskFileParser.Parse("bogus line"));
        Assert.Contains("'bogus'", ex.Message);
    }

    /// <summary>"run <command>" on one line is a single Run command with Command set to the rest of the line.</summary>
    [Fact]
    public void Parse_RunWithInlineCommand_ParsesAsSingleCommand()
    {
        TaskDocument doc = TaskFileParser.Parse("script S\n    run echo hi");

        TaskCommand command = Assert.Single(doc.Scripts[0].Commands);
        Assert.Equal(ScriptInstruction.Run, command.Instruction);
        Assert.Equal("echo hi", command.Command);
    }

    /// <summary>A bare "run" (nothing after it) reads every deeper-indented following line as a multi-line body, dedented by its own common leading indentation.</summary>
    [Fact]
    public void Parse_BareRun_ReadsIndentedBlockAsDedentedBody()
    {
        TaskDocument doc = TaskFileParser.Parse(
            "script S\n" +
            "    run\n" +
            "        line one\n" +
            "        line two\n" +
            "    print done");

        Assert.Equal(2, doc.Scripts[0].Commands.Count);
        Assert.Equal("line one\nline two", doc.Scripts[0].Commands[0].Command);
        Assert.Equal(ScriptInstruction.Print, doc.Scripts[0].Commands[1].Instruction);
    }

    /// <summary>Blank lines inside a multi-line body are kept verbatim (only trailing blank lines at the end of the block are trimmed).</summary>
    [Fact]
    public void Parse_BareRun_KeepsInteriorBlankLinesButTrimsTrailingOnes()
    {
        TaskDocument doc = TaskFileParser.Parse(
            "script S\n" +
            "    run\n" +
            "        line one\n" +
            "\n" +
            "        line two\n" +
            "\n" +
            "\n");

        Assert.Equal("line one\n\nline two", doc.Scripts[0].Commands[0].Command);
    }

    /// <summary>A bare "run" with no following indented block at all is a syntax error.</summary>
    [Fact]
    public void Parse_BareRunWithNoBody_Throws() =>
        Assert.Throws<FormatException>(() => TaskFileParser.Parse("script S\n    run\n    print after"));

    /// <summary>"print <text>" becomes a Print command whose Command is the trimmed rest of the line.</summary>
    [Fact]
    public void Parse_Print_SetsCommandText()
    {
        TaskDocument doc = TaskFileParser.Parse("script S\n    print   hello  ");

        Assert.Equal(ScriptInstruction.Print, doc.Scripts[0].Commands[0].Instruction);
        Assert.Equal("hello", doc.Scripts[0].Commands[0].Command);
    }

    /// <summary>"wait <seconds>" parses a culture-invariant floating-point value.</summary>
    [Fact]
    public void Parse_Wait_ParsesSeconds()
    {
        TaskDocument doc = TaskFileParser.Parse("script S\n    wait 1.5");

        Assert.Equal(ScriptInstruction.Wait, doc.Scripts[0].Commands[0].Instruction);
        Assert.Equal(1.5, doc.Scripts[0].Commands[0].Seconds);
    }

    /// <summary>"wait" with a non-numeric or missing argument is a syntax error.</summary>
    [Theory]
    [InlineData("wait")]
    [InlineData("wait not-a-number")]
    [InlineData("wait 1 2")]
    public void Parse_InvalidWait_Throws(string line) =>
        Assert.Throws<FormatException>(() => TaskFileParser.Parse($"script S\n    {line}"));

    /// <summary>"move <target> -> <destination> [copy] [overwrite]" sets all four fields; flags default to false when omitted.</summary>
    [Fact]
    public void Parse_Move_ParsesTargetDestinationAndFlags()
    {
        TaskDocument doc = TaskFileParser.Parse("script S\n    move a.txt -> b.txt copy overwrite");

        TaskCommand command = doc.Scripts[0].Commands[0];
        Assert.Equal(ScriptInstruction.Move, command.Instruction);
        Assert.Equal("a.txt", command.Target);
        Assert.Equal("b.txt", command.Destination);
        Assert.True(command.Copy);
        Assert.True(command.Overwrite);
    }

    /// <summary>"move" without the "->" separator (or too few tokens) is a syntax error.</summary>
    [Theory]
    [InlineData("move a.txt b.txt")]
    [InlineData("move a.txt")]
    public void Parse_MoveWithoutArrow_Throws(string line) =>
        Assert.Throws<FormatException>(() => TaskFileParser.Parse($"script S\n    {line}"));

    /// <summary>An unrecognized trailing token on "move" is a syntax error, not silently ignored.</summary>
    [Fact]
    public void Parse_MoveWithUnknownFlag_Throws() =>
        Assert.Throws<FormatException>(() => TaskFileParser.Parse("script S\n    move a.txt -> b.txt bogus"));

    /// <summary>"rename <target> -> <newname>" sets Target and Name (Destination is unused for Rename).</summary>
    [Fact]
    public void Parse_Rename_ParsesTargetAndNewName()
    {
        TaskDocument doc = TaskFileParser.Parse("script S\n    rename old.txt -> new.txt");

        TaskCommand command = doc.Scripts[0].Commands[0];
        Assert.Equal(ScriptInstruction.Rename, command.Instruction);
        Assert.Equal("old.txt", command.Target);
        Assert.Equal("new.txt", command.Name);
    }

    /// <summary>"rename" needs exactly target, "->", and a new name - any other token count is a syntax error.</summary>
    [Fact]
    public void Parse_RenameWithWrongArity_Throws() =>
        Assert.Throws<FormatException>(() => TaskFileParser.Parse("script S\n    rename a.txt -> b.txt extra"));

    /// <summary>"file <target> [overwrite] [conditional]" with an indented body captures Content dedented, same as a bare "run" block.</summary>
    [Fact]
    public void Parse_File_ParsesFlagsAndIndentedContent()
    {
        TaskDocument doc = TaskFileParser.Parse(
            "script S\n" +
            "    file out.txt overwrite conditional\n" +
            "        hello\n" +
            "        world");

        TaskCommand command = doc.Scripts[0].Commands[0];
        Assert.Equal(ScriptInstruction.Create, command.Instruction);
        Assert.Equal(CreateEntryKind.File, command.EntryKind);
        Assert.Equal("out.txt", command.Target);
        Assert.True(command.Overwrite);
        Assert.True(command.Conditional);
        Assert.Equal("hello\nworld", command.Content);
    }

    /// <summary>"file" with no body at all leaves Content empty rather than throwing (unlike a bare "run").</summary>
    [Fact]
    public void Parse_FileWithoutBody_HasEmptyContent()
    {
        TaskDocument doc = TaskFileParser.Parse("script S\n    file out.txt");

        Assert.Equal("", doc.Scripts[0].Commands[0].Content);
    }

    /// <summary>"folder <target> [overwrite] [conditional]" has no body to read, unlike "file".</summary>
    [Fact]
    public void Parse_Folder_ParsesFlags()
    {
        TaskDocument doc = TaskFileParser.Parse("script S\n    folder build overwrite");

        TaskCommand command = doc.Scripts[0].Commands[0];
        Assert.Equal(ScriptInstruction.Create, command.Instruction);
        Assert.Equal(CreateEntryKind.Folder, command.EntryKind);
        Assert.Equal("build", command.Target);
        Assert.True(command.Overwrite);
        Assert.False(command.Conditional);
    }

    /// <summary>"file"/"folder" without any target token is a syntax error.</summary>
    [Theory]
    [InlineData("file overwrite")]
    [InlineData("folder")]
    public void Parse_EntryCommandWithoutTarget_Throws(string line) =>
        Assert.Throws<FormatException>(() => TaskFileParser.Parse($"script S\n    {line}"));

    /// <summary>"delete <target>" and "purge <target>" each take exactly one path token.</summary>
    [Theory]
    [InlineData("delete build", ScriptInstruction.Delete)]
    [InlineData("purge build", ScriptInstruction.Purge)]
    public void Parse_DeleteOrPurge_ParsesSingleTarget(string line, ScriptInstruction expected)
    {
        TaskDocument doc = TaskFileParser.Parse($"script S\n    {line}");

        TaskCommand command = doc.Scripts[0].Commands[0];
        Assert.Equal(expected, command.Instruction);
        Assert.Equal("build", command.Target);
    }

    /// <summary>"delete"/"purge" with zero or more than one target token is a syntax error.</summary>
    [Theory]
    [InlineData("delete")]
    [InlineData("delete a b")]
    [InlineData("purge a b")]
    public void Parse_DeleteOrPurgeWithWrongArity_Throws(string line) =>
        Assert.Throws<FormatException>(() => TaskFileParser.Parse($"script S\n    {line}"));

    /// <summary>"cd <path>" sets SetContext's Path field.</summary>
    [Fact]
    public void Parse_Cd_SetsPath()
    {
        TaskDocument doc = TaskFileParser.Parse("script S\n    cd ../sibling");

        TaskCommand command = doc.Scripts[0].Commands[0];
        Assert.Equal(ScriptInstruction.SetContext, command.Instruction);
        Assert.Equal("../sibling", command.Path);
    }

    /// <summary>"output <column> <row>" is 1-based on the page but converted to 0-based Row/Column on the script, and is never added to Commands.</summary>
    [Fact]
    public void Parse_Output_SetsZeroBasedRowColumnAndIsNotACommand()
    {
        TaskDocument doc = TaskFileParser.Parse("script S\n    output 2 3\n    print hi");

        TaskScript script = doc.Scripts[0];
        Assert.Equal(1, script.Column);
        Assert.Equal(2, script.Row);
        Assert.Single(script.Commands);
    }

    /// <summary>The last "output" line in a script wins if there's more than one.</summary>
    [Fact]
    public void Parse_MultipleOutputLines_LastOneWins()
    {
        TaskDocument doc = TaskFileParser.Parse("script S\n    output 1 1\n    output 5 6\n    print hi");

        Assert.Equal(4, doc.Scripts[0].Column);
        Assert.Equal(5, doc.Scripts[0].Row);
    }

    /// <summary>"output" values below 1 (the documented 1-based minimum) are syntax errors, as is a non-integer or wrong-arity argument list.</summary>
    [Theory]
    [InlineData("output 0 1")]
    [InlineData("output 1 0")]
    [InlineData("output 1")]
    [InlineData("output a b")]
    public void Parse_InvalidOutput_Throws(string line) =>
        Assert.Throws<FormatException>(() => TaskFileParser.Parse($"script S\n    {line}"));

    /// <summary>An unrecognized instruction keyword inside a script is a syntax error naming it.</summary>
    [Fact]
    public void Parse_UnknownInstruction_Throws()
    {
        FormatException ex = Assert.Throws<FormatException>(() => TaskFileParser.Parse("script S\n    bogus thing"));
        Assert.Contains("'bogus'", ex.Message);
    }

    /// <summary>A script's body indent level is set by its first command line; any sibling command at a different indent is a syntax error (compared by raw character count, not a fixed tab width).</summary>
    [Fact]
    public void Parse_InconsistentSiblingIndentation_Throws()
    {
        FormatException ex = Assert.Throws<FormatException>(() => TaskFileParser.Parse(
            "script S\n" +
            "    print one\n" +
            "        print two"));

        Assert.Contains("Inconsistent indentation", ex.Message);
    }

    /// <summary>A script ends at the first line indented back to its header's own level or shallower - a sibling "script" header correctly closes the previous script rather than being swallowed as one of its commands.</summary>
    [Fact]
    public void Parse_ScriptBody_EndsAtHeaderLevelSibling()
    {
        TaskDocument doc = TaskFileParser.Parse(
            """
            script One
                print a
            script Two
                print b
            """);

        Assert.Equal(2, doc.Scripts.Count);
        Assert.Equal("a", doc.Scripts[0].Commands[0].Command);
        Assert.Equal("b", doc.Scripts[1].Commands[0].Command);
    }

    /// <summary>Whitespace-separated tokens support "double-quoted strings" with embedded spaces, and backslash-escaped '"' and '\' inside them.</summary>
    [Fact]
    public void Parse_QuotedTokens_SupportEmbeddedSpacesAndEscapes()
    {
        TaskDocument doc = TaskFileParser.Parse(
            """
            script S
                move "a file.txt" -> "b \"quoted\" file.txt"
            """);

        TaskCommand command = doc.Scripts[0].Commands[0];
        Assert.Equal("a file.txt", command.Target);
        Assert.Equal("b \"quoted\" file.txt", command.Destination);
    }

    /// <summary>An unterminated quoted string is a syntax error rather than reading past the end of the line.</summary>
    [Fact]
    public void Parse_UnterminatedQuotedString_Throws() =>
        Assert.Throws<FormatException>(() => TaskFileParser.Parse("script S\n    cd \"unterminated"));

    /// <summary>An empty document (or one that's only blank lines/comments) parses to zero variables and zero scripts rather than throwing.</summary>
    [Fact]
    public void Parse_EmptyDocument_ProducesNoVariablesOrScripts()
    {
        TaskDocument doc = TaskFileParser.Parse("\n# just a comment\n\n");

        Assert.Empty(doc.Variables);
        Assert.Empty(doc.Scripts);
    }
}
