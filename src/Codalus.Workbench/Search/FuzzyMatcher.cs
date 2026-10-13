namespace Codalus.Workbench.Search;

/// <summary>A fuzzy match: higher scores are better; <see cref="Positions"/> are the matched character indices.</summary>
public sealed record FuzzyMatch(int Score, IReadOnlyList<int> Positions);

/// <summary>
/// Case-insensitive subsequence matching for the command palette and quick pickers. Matches at word starts
/// (after a space, separator or a lower→upper camelCase step), consecutive runs and an early first match score
/// higher, so "shp" ranks "Show Palette" above "fresh paint".
/// </summary>
public static class FuzzyMatcher
{
    public static FuzzyMatch? Match(string query, string text)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return new FuzzyMatch(0, []);
        }

        // Greedy forward pass with a preference for word starts: for each query character, take the next word
        // start that matches if one exists before the next plain match would force a worse alignment.
        var positions = new List<int>(query.Length);
        var score = 0;
        var t = 0;
        var previous = -2;
        foreach (var qc in query)
        {
            if (qc == ' ')
            {
                continue;
            }

            var plain = -1;
            var wordStart = -1;
            for (var i = t; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(qc))
                {
                    continue;
                }

                if (plain < 0)
                {
                    plain = i;
                }

                if (IsWordStart(text, i))
                {
                    wordStart = i;
                    break;
                }
            }

            if (plain < 0)
            {
                return null;
            }

            // Prefer continuing a consecutive run; otherwise prefer a word start.
            var chosen = plain == previous + 1 ? plain : wordStart >= 0 ? wordStart : plain;
            score += 1;
            if (chosen == previous + 1)
            {
                score += 5;
            }

            if (IsWordStart(text, chosen))
            {
                score += 8;
            }

            if (text[chosen] == qc)
            {
                score += 1;
            }

            positions.Add(chosen);
            previous = chosen;
            t = chosen + 1;
        }

        score -= positions.Count > 0 ? Math.Min(positions[0], 10) : 0;
        score -= Math.Max(0, text.Length - query.Length) / 8;
        if (text.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }
        else if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        return new FuzzyMatch(score, positions);
    }

    private static bool IsWordStart(string text, int i)
    {
        if (i == 0)
        {
            return true;
        }

        var previous = text[i - 1];
        return previous is ' ' or '.' or ':' or '-' or '_' or '/' or '(' || (char.IsLower(previous) && char.IsUpper(text[i]));
    }
}
