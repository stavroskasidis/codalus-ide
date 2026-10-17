using System.Runtime.CompilerServices;

namespace Codalus.Workbench.Search;

/// <summary>
/// A Code Search result (M47): what it is, its name with the matched characters, where it is (container and project
/// for a symbol, the folder for a file, the line's text for a text match), and where it points. Positions are
/// zero-based; <see cref="Uri"/> is set for documents that aren't files (decompiled, generated).
/// </summary>
public sealed record CodeSearchResult(CodeSearchKind Kind, string Name)
{
    /// <summary>The LSP symbol kind (types and members), for the icon.</summary>
    public int SymbolKind { get; init; }

    /// <summary>The containing type of a member, or a type's namespace.</summary>
    public string? Container { get; init; }

    public string? Project { get; init; }

    public string? Path { get; init; }

    public string? Uri { get; init; }

    public int Line { get; init; }

    public int Column { get; init; }

    /// <summary>The match's length in a text result (selected in the preview).</summary>
    public int Length { get; init; }

    /// <summary>A text result's line, trimmed.</summary>
    public string? LineText { get; init; }

    /// <summary>The matched characters of <see cref="Name"/> (of <see cref="LineText"/> for a text result).</summary>
    public IReadOnlyList<int> Highlights { get; init; } = [];

    /// <summary>A type of a referenced assembly ("include external items").</summary>
    public bool IsExternal { get; init; }

    /// <summary>The ranking's score: higher first.</summary>
    public int Score { get; init; }

    /// <summary>Where the result points, for de-duplicating the same declaration from two sources.</summary>
    public string Key => $"{Kind}|{Name}|{Path ?? Uri}|{Line}";
}

/// <summary>What ranking knows besides the match: recently opened files, the active document and its project.</summary>
public sealed record CodeSearchRankContext(IReadOnlyList<string> RecentFiles, string? ActiveDocument = null, string? ActiveProject = null)
{
    public static CodeSearchRankContext Empty { get; } = new([]);
}

/// <summary>
/// Code Search's ranking: the match's quality (<see cref="FuzzyMatcher"/>: exact, prefix, word starts and runs score
/// higher), then the kind (types, files, members, then text), recently opened files and the active project.
/// </summary>
public static class CodeSearchRanker
{
    public static int KindWeight(CodeSearchKind kind, bool external) => kind switch
    {
        CodeSearchKind.Line => 1000,
        CodeSearchKind.Type => external ? 4 : 12,
        CodeSearchKind.File => 10,
        CodeSearchKind.Member => external ? -2 : 6,
        _ => -20,
    };

    /// <summary>The score of a result whose name matched with <paramref name="matchScore"/>.</summary>
    public static int Score(CodeSearchResult result, int matchScore, CodeSearchRankContext context) =>
        Score(result.Kind, result.Path, result.Project, result.IsExternal, matchScore, context);

    /// <summary>The score of a result of <paramref name="kind"/> in <paramref name="path"/>, without building it.</summary>
    public static int Score(CodeSearchKind kind, string? path, string? project, bool external, int matchScore, CodeSearchRankContext context) =>
        matchScore + KindWeight(kind, external) + ContextBoost(path, project, context);

    /// <summary>What a result's file and project add: a recently opened file (the more recent, the more), the active document, the active project.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static int ContextBoost(string? path, string? project, CodeSearchRankContext context)
    {
        var score = 0;
        if (path is not null)
        {
            for (var i = 0; i < context.RecentFiles.Count && i < 8; i++)
            {
                if (SameIgnoringCase(context.RecentFiles[i], path))
                {
                    score += Math.Max(3, 15 - 2 * i);
                    break;
                }
            }

            if (context.ActiveDocument is { } document && SameIgnoringCase(document, path))
            {
                score += 4;
            }
        }

        if (context.ActiveProject is { } active && SameIgnoringCase(active, project))
        {
            score += 6;
        }

        return score;
    }

    /// <summary>
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> equality, the ends compared first: the paths of a folder share
    /// a long start and differ at their ends, and Code Search compares every file it ranks with the recent ones (M47).
    /// </summary>
    private static bool SameIgnoringCase(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a.Length != b.Length)
        {
            return false;
        }

        // Not between the two halves of a surrogate pair, which compare as one.
        var tail = Math.Max(0, a.Length - 16);
        while (tail > 0 && (char.IsLowSurrogate(a[tail]) || char.IsLowSurrogate(b[tail])))
        {
            tail--;
        }

        return a.AsSpan(tail).Equals(b.AsSpan(tail), StringComparison.OrdinalIgnoreCase)
            && a.AsSpan(0, tail).Equals(b.AsSpan(0, tail), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Orders results best first: score, then shorter names, then by name, and keeps one of each declaration.</summary>
    public static IReadOnlyList<CodeSearchResult> Order(IEnumerable<CodeSearchResult> results, int limit) =>
        [.. results
            .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(r => r.Score)!)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Name.Length)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Line)
            .Take(limit)];

    /// <summary>
    /// A quick test that every character of <paramref name="query"/> occurs in <paramref name="text"/> in order (case
    /// ignored), so most of a large index is skipped without the matcher's allocations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MayMatch(string query, string text) => MayMatch(query.AsSpan(), text.AsSpan());

    /// <inheritdoc cref="MayMatch(string, string)"/>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool MayMatch(ReadOnlySpan<char> query, ReadOnlySpan<char> text)
    {
        var t = 0;
        foreach (var q in query)
        {
            if (q == ' ')
            {
                continue;
            }

            var lower = char.ToLowerInvariant(q);
            var upper = char.ToUpperInvariant(q);
            var found = lower == upper ? text[t..].IndexOf(lower) : text[t..].IndexOfAny(lower, upper);
            if (found < 0)
            {
                return false;
            }

            t += found + 1;
        }

        return true;
    }
}
