namespace AutoDev.ViewModels.Content;

/// <summary>
/// Plain non-overlapping substring search (advances past each match by its own length, so e.g. searching
/// "aa" in "aaaa" finds 2 matches, not 3) - no regex, matching this codebase's preference for a small
/// hand-rolled scan over pulling in a search engine (see FileSearchViewModel's own content search) for
/// something this simple. Shared by EditTabViewModel's plain-text find (searching Content, a single string)
/// and EditTabView's markdown-preview find (searching each rendered CTextBlock's own Text in turn) - see
/// EditTabViewModel.PreviewSearchInvalidated for why the two need genuinely separate match-finding despite
/// sharing this same core algorithm.
/// </summary>
internal static class TextSearch
{
    public static List<int> FindAllMatches(string content, string pattern, bool matchCase, bool matchWholeWord)
    {
        List<int> offsets = [];
        if (pattern.Length == 0 || pattern.Length > content.Length)
        {
            return offsets;
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = 0;
        while (index <= content.Length - pattern.Length)
        {
            var found = content.IndexOf(pattern, index, comparison);
            if (found < 0)
            {
                break;
            }

            if (!matchWholeWord || IsWholeWordMatch(content, found, pattern.Length))
            {
                offsets.Add(found);
            }

            index = found + pattern.Length;
        }

        return offsets;
    }

    private static bool IsWholeWordMatch(string content, int start, int length)
    {
        var leftBoundary = start == 0 || !IsWordChar(content[start - 1]);
        var end = start + length;
        var rightBoundary = end == content.Length || !IsWordChar(content[end]);
        return leftBoundary && rightBoundary;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
