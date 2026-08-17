namespace AutoDev.Tests.Core.Services;

/// <summary>Covers VariableSubstitution's %VAR% replacement syntax, including the undefined-variable failure mode.</summary>
public sealed class VariableSubstitutionTests
{
    /// <summary>A single %VAR% reference is replaced with its value from the lookup dictionary.</summary>
    [Fact]
    public void Substitute_SingleReference_IsReplaced()
    {
        Dictionary<string, string> variables = new() { ["NAME"] = "world" };

        string result = VariableSubstitution.Substitute("Hello, %NAME%!", variables);

        Assert.Equal("Hello, world!", result);
    }

    /// <summary>Multiple distinct references, including two adjacent with no separator, are all resolved in one pass.</summary>
    [Fact]
    public void Substitute_MultipleAdjacentReferences_AreAllReplaced()
    {
        Dictionary<string, string> variables = new() { ["A"] = "foo", ["B"] = "bar" };

        string result = VariableSubstitution.Substitute("%A%%B%", variables);

        Assert.Equal("foobar", result);
    }

    /// <summary>The same variable can be referenced more than once and each occurrence is substituted independently.</summary>
    [Fact]
    public void Substitute_RepeatedReference_IsReplacedEveryTime()
    {
        Dictionary<string, string> variables = new() { ["X"] = "1" };

        string result = VariableSubstitution.Substitute("%X% + %X% = 2", variables);

        Assert.Equal("1 + 1 = 2", result);
    }

    /// <summary>Text with no %VAR% syntax at all passes through unchanged.</summary>
    [Fact]
    public void Substitute_NoReferences_ReturnsTextUnchanged()
    {
        string result = VariableSubstitution.Substitute("plain text, no percent signs", new Dictionary<string, string>());

        Assert.Equal("plain text, no percent signs", result);
    }

    /// <summary>A reference to a variable that isn't in the lookup throws, naming the undefined variable.</summary>
    [Fact]
    public void Substitute_UndefinedVariable_Throws()
    {
        FormatException ex = Assert.Throws<FormatException>(() =>
            VariableSubstitution.Substitute("%MISSING%", new Dictionary<string, string>()));

        Assert.Contains("MISSING", ex.Message);
    }

    /// <summary>A lone "%" with no matching closing "%" (or an empty/invalid name) isn't treated as a variable reference at all.</summary>
    [Theory]
    [InlineData("100% done")]
    [InlineData("%1invalid%")]
    public void Substitute_TextThatLooksLikeButIsNotAReference_IsLeftUntouched(string text)
    {
        string result = VariableSubstitution.Substitute(text, new Dictionary<string, string>());

        Assert.Equal(text, result);
    }
}
