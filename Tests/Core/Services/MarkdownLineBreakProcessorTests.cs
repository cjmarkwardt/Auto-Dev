namespace Tests.Core.Services;

public sealed class MarkdownLineBreakProcessorTests
{
    [Theory]
    [InlineData("Line one<br>Line two", "Line one  \nLine two")]
    [InlineData("Line one<br/>Line two", "Line one  \nLine two")]
    [InlineData("Line one<br />Line two", "Line one  \nLine two")]
    [InlineData("Line one<BR>Line two", "Line one  \nLine two")]
    public void RewritesBrTagsIntoHardLineBreaks(string markdown, string expected) =>
        Assert.Equal(expected, MarkdownLineBreakProcessor.Process(markdown));

    [Fact]
    public void LeavesTextWithNoBrTagUnchanged()
    {
        const string markdown = "Just a plain paragraph with no tags at all.";
        Assert.Equal(markdown, MarkdownLineBreakProcessor.Process(markdown));
    }

    [Fact]
    public void LeavesABrTagInsideAFencedCodeBlockUntouched()
    {
        const string markdown = "Text<br>before\n```html\n<p>Example<br>Text</p>\n```\nText<br>after";

        var result = MarkdownLineBreakProcessor.Process(markdown);

        Assert.Contains("Text  \nbefore", result);
        Assert.Contains("<p>Example<br>Text</p>", result);
        Assert.Contains("Text  \nafter", result);
    }
}
