using System.Globalization;
using Codalus.Workbench.Git;
using Codalus.Workbench.Testing;

namespace Codalus.Shell.Editor;

/// <summary>
/// The CodeLens entries the editor adds to the language service's (deferred from M12a): a member's Git history from
/// blame (the last author and when, and how many authors and changes) and a test's or test class's last outcome.
/// </summary>
public static class LensDetails
{
    /// <summary>
    /// The history of lines <paramref name="startLine"/>–<paramref name="endLine"/> (one-based): <c>Ada, 3 days ago</c>
    /// and <c>2 authors, 5 changes</c>; null when no committed line is in the range.
    /// </summary>
    public static string? Git(IReadOnlyList<BlameRange> blame, int startLine, int endLine, DateTimeOffset now)
    {
        var committed = blame
            .Where(r => !r.IsUncommitted && r.StartLine <= endLine && r.StartLine + r.LineCount - 1 >= startLine)
            .ToList();
        if (committed.Count == 0)
        {
            return null;
        }

        var latest = committed.MaxBy(r => r.Date)!;
        var authors = committed.Select(r => r.Author).Distinct(StringComparer.Ordinal).Count();
        var changes = committed.Select(r => r.Commit).Distinct(StringComparer.Ordinal).Count();
        return $"{latest.Author}, {Ago(now - latest.Date)}  |  {Count(authors, "author")}, {Count(changes, "change")}";
    }

    /// <summary>A test's last outcome (<c>✓ Passed</c>), or a class's (<c>3/4 passing</c>); null for other nodes.</summary>
    public static string? Tests(TestNode node)
    {
        if (node.IsLeaf)
        {
            return node.Outcome switch
            {
                TestOutcome.Passed => node.Result?.Duration is { } duration ? $"✓ Passed ({Duration(duration)})" : "✓ Passed",
                TestOutcome.Failed or TestOutcome.Errored => "✗ Failed",
                TestOutcome.Skipped => "Skipped",
                TestOutcome.Running or TestOutcome.Queued => "Running…",
                _ => "Not run",
            };
        }

        var leaves = node.Leaves().ToList();
        if (leaves.Count == 0)
        {
            return null;
        }

        var ran = leaves.Count(l => l.Outcome is TestOutcome.Passed or TestOutcome.Failed or TestOutcome.Errored);
        if (ran == 0)
        {
            return Count(leaves.Count, "test") + ", not run";
        }

        var failed = leaves.Count(l => l.Outcome.IsFailure());
        var passing = $"{leaves.Count(l => l.Outcome == TestOutcome.Passed)}/{leaves.Count} passing";
        return failed > 0 ? $"✗ {passing}" : $"✓ {passing}";
    }

    /// <summary><c>just now</c>, <c>5 minutes ago</c>, <c>yesterday</c>, <c>3 months ago</c>…</summary>
    public static string Ago(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => Count((int)elapsed.TotalMinutes, "minute") + " ago",
        { TotalDays: < 1 } => Count((int)elapsed.TotalHours, "hour") + " ago",
        { TotalDays: < 2 } => "yesterday",
        { TotalDays: < 31 } => Count((int)elapsed.TotalDays, "day") + " ago",
        { TotalDays: < 365 } => Count((int)(elapsed.TotalDays / 30.4), "month") + " ago",
        _ => Count((int)(elapsed.TotalDays / 365.25), "year") + " ago",
    };

    private static string Duration(TimeSpan duration) =>
        duration.TotalSeconds < 1
            ? ((int)Math.Round(duration.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture) + " ms"
            : duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
