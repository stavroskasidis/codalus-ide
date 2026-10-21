namespace Codalus.Shell.Editor.IntelliSense;

/// <summary>
/// How well a completion item's text matches what was typed, and which of its characters matched (drawn bold). The
/// qualities are AvaloniaEdit's completion list's (MIT), which the list used before: the whole text, then a prefix,
/// then camel-case humps for one or two characters, a substring, and camel-case humps; case-sensitive before not.
/// </summary>
public static class CompletionMatcher
{
    /// <summary>The match quality (8 best, 1 worst, -1 none) and the matched characters' positions in <paramref name="text"/>.</summary>
    public static int Match(string text, string query, out int[] positions)
    {
        positions = [];
        if (query.Length == 0)
        {
            return 6;
        }

        if (string.Equals(text, query, StringComparison.Ordinal))
        {
            positions = Range(0, query.Length);
            return 8;
        }

        if (string.Equals(text, query, StringComparison.OrdinalIgnoreCase))
        {
            positions = Range(0, query.Length);
            return 7;
        }

        if (text.StartsWith(query, StringComparison.Ordinal))
        {
            positions = Range(0, query.Length);
            return 6;
        }

        if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            positions = Range(0, query.Length);
            return 5;
        }

        int[]? humps = null;
        if (query.Length <= 2)
        {
            humps = CamelCase(text, query);
            if (humps is not null)
            {
                positions = humps;
                return 4;
            }
        }

        var index = text.IndexOf(query, StringComparison.Ordinal);
        if (index >= 0)
        {
            positions = Range(index, query.Length);
            return 3;
        }

        index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            positions = Range(index, query.Length);
            return 2;
        }

        humps ??= query.Length <= 2 ? null : CamelCase(text, query);
        if (humps is not null)
        {
            positions = humps;
            return 1;
        }

        return -1;
    }

    /// <summary>
    /// The positions of the query's letters as the text's humps (its first letter and its upper-case letters, in order:
    /// "cct" and "CCT" match "camelCaseText"), or null.
    /// </summary>
    private static int[]? CamelCase(string text, string query)
    {
        var positions = new List<int>(query.Length);
        for (var i = 0; i < text.Length && positions.Count < query.Length; i++)
        {
            if (i > 0 && !char.IsUpper(text[i]))
            {
                continue;
            }

            if (char.ToUpperInvariant(text[i]) != char.ToUpperInvariant(query[positions.Count]))
            {
                return null;
            }

            positions.Add(i);
        }

        return positions.Count == query.Length ? [.. positions] : null;
    }

    private static int[] Range(int start, int count)
    {
        var range = new int[count];
        for (var i = 0; i < count; i++)
        {
            range[i] = start + i;
        }

        return range;
    }
}
