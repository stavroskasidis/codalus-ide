using System.Runtime.CompilerServices;
using System.Text;

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

        var positions = new List<int>(query.Length);
        return Run(query, text, positions) is { } score ? new FuzzyMatch(score, positions) : null;
    }

    /// <summary>
    /// <see cref="Match"/>'s score without the positions, allocating nothing: Code Search scores a large index with
    /// it and asks <see cref="Match"/> for the highlights of the results it keeps (M47). <paramref name="query"/> must
    /// be trimmed and not empty.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryScore(string query, string text, out int score) => TryScore(query.AsSpan(), text.AsSpan(), out score);

    /// <inheritdoc cref="TryScore(string, string, out int)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryScore(ReadOnlySpan<char> query, ReadOnlySpan<char> text, out int score)
    {
        var result = Run(query, text, null);
        score = result ?? 0;
        return result is not null;
    }

    // Compiled fully optimized at once: Code Search runs it over a large index a few times, too few for tiered
    // compilation to replace its first, unoptimized code in time (M47).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int? Run(ReadOnlySpan<char> query, ReadOnlySpan<char> text, List<int>? positions)
    {
        // Greedy forward pass with a preference for word starts: for each query character, take the next word
        // start that matches if one exists before the next plain match would force a worse alignment.
        var first = -1;
        var count = 0;
        var score = 0;
        var t = 0;
        var previous = -2;
        var ascii = Ascii.IsValid(text);
        foreach (var qc in query)
        {
            if (qc == ' ')
            {
                continue;
            }

            var lower = char.ToLowerInvariant(qc);
            var plain = -1;
            var wordStart = -1;
            if (ascii)
            {
                // An ASCII text (nearly every name): the characters that lower to the query's are its two cases,
                // found a vector at a time.
                if (lower < 128)
                {
                    var upper = lower is >= 'a' and <= 'z' ? (char)(lower - 32) : lower;
                    for (var i = t; i < text.Length;)
                    {
                        var found = upper == lower ? text[i..].IndexOf(lower) : text[i..].IndexOfAny(lower, upper);
                        if (found < 0)
                        {
                            break;
                        }

                        i += found;
                        if (plain < 0)
                        {
                            plain = i;
                        }

                        if (IsWordStart(text, i))
                        {
                            wordStart = i;
                            break;
                        }

                        i++;
                    }
                }
            }
            else
            {
                for (var i = t; i < text.Length; i++)
                {
                    if (char.ToLowerInvariant(text[i]) != lower)
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

            positions?.Add(chosen);
            if (count++ == 0)
            {
                first = chosen;
            }

            previous = chosen;
            t = chosen + 1;
        }

        score -= count > 0 ? Math.Min(first, 10) : 0;
        score -= Math.Max(0, text.Length - query.Length) / 8;
        if (text.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 50;
        }
        else if (text.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        return score;
    }

    private static bool IsWordStart(ReadOnlySpan<char> text, int i)
    {
        if (i == 0)
        {
            return true;
        }

        var previous = text[i - 1];
        return previous is ' ' or '.' or ':' or '-' or '_' or '/' or '(' || (char.IsLower(previous) && char.IsUpper(text[i]));
    }
}
