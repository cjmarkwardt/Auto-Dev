namespace AutoDev.Tests.CodexCli;

/// <summary>Covers TomlString.Quote's escaping of a value for embedding as a TOML basic string.</summary>
public sealed class TomlStringTests
{
    /// <summary>A plain value with nothing to escape is simply wrapped in double quotes.</summary>
    [Fact]
    public void Quote_PlainValue_IsWrappedInDoubleQuotes() =>
        Assert.Equal("\"hello\"", TomlString.Quote("hello"));

    /// <summary>An embedded double quote is backslash-escaped.</summary>
    [Fact]
    public void Quote_EmbeddedDoubleQuote_IsEscaped() =>
        Assert.Equal("\"say \\\"hi\\\"\"", TomlString.Quote("say \"hi\""));

    /// <summary>An embedded backslash is itself escaped, and escaped BEFORE quote-escaping so a literal backslash never gets mistaken for part of a later quote's escape sequence.</summary>
    [Fact]
    public void Quote_EmbeddedBackslash_IsEscaped() =>
        Assert.Equal("\"C:\\\\path\"", TomlString.Quote("C:\\path"));

    /// <summary>A value with both backslashes and quotes escapes each independently without one interfering with the other.</summary>
    [Fact]
    public void Quote_BackslashesAndQuotesTogether_AreBothEscaped() =>
        Assert.Equal("\"\\\\\\\"\"", TomlString.Quote("\\\""));

    /// <summary>An empty value quotes to an empty TOML basic string.</summary>
    [Fact]
    public void Quote_EmptyValue_ReturnsEmptyQuotedString() =>
        Assert.Equal("\"\"", TomlString.Quote(""));
}
