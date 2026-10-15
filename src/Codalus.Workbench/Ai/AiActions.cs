using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Codalus.Workbench.Ai;

/// <summary>A fix for a diagnostic: the lines it replaces (1-based, inclusive) and their new text.</summary>
public sealed record AiFix(int StartLine, int EndLine, string NewText);

/// <summary>
/// AI actions on existing surfaces (M28b, ADR 0062): commit messages, pull request descriptions and fixes for
/// diagnostics, answered whole; and the questions that open in AI Chat (an exception, a failing test, tests to write, a
/// profile). Each runs only when the user asks, through <see cref="AiService"/>.
/// </summary>
public static partial class AiActions
{
    public const string CommitMessagePrompt =
        """
        You write git commit messages. Reply with only the message: a subject line of at most 72 characters in the imperative mood ("Add", "Fix"), then, only when it adds something, a blank line and a short body that says why.
        Follow the style of the repository's recent subjects when they are given. No code fences, no quotes around the message.
        """;

    public const string PullRequestPrompt =
        """
        You write pull request descriptions. Reply with a title on the first line (at most 72 characters, no prefix), a blank line, then a Markdown description: what changes and why, in a few bullets, and anything a reviewer should look at.
        No code fences around the whole reply.
        """;

    public const string FixPrompt =
        """
        You fix a compiler or analyzer diagnostic in the user's code. The context shows the lines around it, each starting with its line number and a bar.
        Reply with one fenced code block holding the corrected lines from the first shown line to the last, without line numbers, and nothing else. Change only what the fix needs.
        """;

    /// <summary>Asked in AI Chat about the debugger's exception, with the exception and the frame's code.</summary>
    public const string ExplainExceptionQuestion = "Why was this exception thrown, and how do I fix it?";

    /// <summary>Asked in AI Chat about a failing test.</summary>
    public const string ExplainTestQuestion = "Why does this test fail, and what should change: the test or the code?";

    /// <summary>Asked in AI Chat to write tests for the selection.</summary>
    public const string GenerateTestsQuestion = "Write unit tests for this code with {0}. Cover the main cases and the edge cases, and name the test file in the code block (path=…).";

    /// <summary>Asked in AI Chat about a CPU profile.</summary>
    public const string SummarizeProfileQuestion = "Summarize this CPU profile: where does the time go, and what are the best opportunities to make it faster?";

    /// <summary>
    /// The diff without the files the boundary excludes (by the path in each <c>diff --git</c> header, relative to
    /// <paramref name="repositoryRoot"/>), and the paths it left out.
    /// </summary>
    public static async Task<(string Diff, IReadOnlyList<string> Excluded)> FilterDiffAsync(AiService ai, string diff, string repositoryRoot, CancellationToken cancellationToken)
    {
        var kept = new StringBuilder();
        var excluded = new List<string>();
        foreach (var section in SplitDiff(diff))
        {
            if (section.Path is { } path && await ai.Exclusions.IsExcludedAsync(Path.Combine(repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar)), ai.WorkspaceRoot ?? repositoryRoot, cancellationToken).ConfigureAwait(false))
            {
                excluded.Add(path);
                continue;
            }

            kept.Append(section.Text);
        }

        return (kept.ToString(), excluded);
    }

    /// <summary>A diff's sections, one per file (text before the first header is a section without a path).</summary>
    public static IEnumerable<(string? Path, string Text)> SplitDiff(string diff)
    {
        var matches = DiffHeader().Matches(diff);
        if (matches.Count == 0)
        {
            if (diff.Length > 0)
            {
                yield return (null, diff);
            }

            yield break;
        }

        if (matches[0].Index > 0)
        {
            yield return (null, diff[..matches[0].Index]);
        }

        for (var i = 0; i < matches.Count; i++)
        {
            var end = i + 1 < matches.Count ? matches[i + 1].Index : diff.Length;
            yield return (matches[i].Groups["path"].Value, diff[matches[i].Index..end]);
        }
    }

    /// <summary>A commit message for the staged <paramref name="diff"/>.</summary>
    public static async Task<string> CommitMessageAsync(AiService ai, string diff, string repositoryRoot, IReadOnlyList<string> recentSubjects, CancellationToken cancellationToken)
    {
        var (filtered, excluded) = await FilterDiffAsync(ai, diff, repositoryRoot, cancellationToken).ConfigureAwait(false);
        var items = new List<AiContextItem> { new(AiContextKind.Changes, "staged changes", filtered) { Language = "diff" } };
        var question = new StringBuilder("Write the commit message for these staged changes.");
        if (recentSubjects.Count > 0)
        {
            question.Append("\n\nRecent subjects in this repository:\n").AppendJoin('\n', recentSubjects.Take(10).Select(s => "- " + s));
        }

        if (excluded.Count > 0)
        {
            question.Append("\n\nAlso changed (not shown): ").AppendJoin(", ", excluded);
        }

        var (reply, _) = await ai.AskAsync("commit message", CommitMessagePrompt, question.ToString(), items, cancellationToken, maxOutputTokens: 400).ConfigureAwait(false);
        return Unfence(reply.Text).Trim().Trim('"');
    }

    /// <summary>A pull request's title and description from its commits and diff.</summary>
    public static async Task<(string Title, string Description)> PullRequestAsync(AiService ai, IReadOnlyList<string> commits, string diff, string repositoryRoot, CancellationToken cancellationToken)
    {
        var (filtered, excluded) = await FilterDiffAsync(ai, diff, repositoryRoot, cancellationToken).ConfigureAwait(false);
        var items = new List<AiContextItem>
        {
            new(AiContextKind.Changes, "commits", string.Join('\n', commits)),
            new(AiContextKind.Changes, "diff", filtered) { Language = "diff" },
        };
        var question = excluded.Count > 0
            ? "Write the pull request's title and description. Also changed (not shown): " + string.Join(", ", excluded)
            : "Write the pull request's title and description.";
        var (reply, _) = await ai.AskAsync("pull request description", PullRequestPrompt, question, items, cancellationToken, maxOutputTokens: 1200).ConfigureAwait(false);
        var text = Unfence(reply.Text).Trim();
        var newline = text.IndexOf('\n', StringComparison.Ordinal);
        var title = (newline < 0 ? text : text[..newline]).Trim().TrimStart('#', ' ').Trim('"');
        var description = newline < 0 ? string.Empty : text[(newline + 1)..].Trim();
        return (title, description);
    }

    /// <summary>
    /// The lines shown around a diagnostic's line (1-based): <paramref name="radius"/> above and below, within the file.
    /// </summary>
    public static (int StartLine, int EndLine) FixRegion(string[] lines, int line, int radius = 12) =>
        (Math.Max(1, line - radius), Math.Min(lines.Length, line + radius));

    /// <summary>A fix for the diagnostic at <paramref name="line"/> of <paramref name="text"/>, or null when the reply has none.</summary>
    public static async Task<AiFix?> FixAsync(AiService ai, string path, string? languageId, string text, int line, string code, string message, CancellationToken cancellationToken)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var (start, end) = FixRegion(lines, line);
        var numbered = new StringBuilder();
        for (var i = start; i <= end; i++)
        {
            numbered.Append(i.ToString(CultureInfo.InvariantCulture)).Append(" | ").Append(lines[i - 1]).Append('\n');
        }

        var items = new List<AiContextItem>
        {
            new(AiContextKind.Selection, Path.GetFileName(path), numbered.ToString()) { Path = path, StartLine = start, Language = languageId },
        };
        var question = string.Create(CultureInfo.InvariantCulture, $"Fix {code} at line {line}: {message}");
        var (reply, context) = await ai.AskAsync("fix", FixPrompt, question, items, cancellationToken, maxOutputTokens: 2000).ConfigureAwait(false);
        if (context.Included.Count == 0)
        {
            return null;
        }

        if (AiCodeBlocks.Parse(reply.Text) is not [var block, ..])
        {
            return null;
        }

        // Models sometimes keep the line numbers: take them off when every line has one.
        var newLines = block.Code.TrimEnd('\n').Split('\n');
        if (newLines.All(l => LineNumber().IsMatch(l)))
        {
            newLines = [.. newLines.Select(l => LineNumber().Replace(l, "", 1))];
        }

        return new AiFix(start, end, string.Join('\n', newLines));
    }

    /// <summary>The text of a reply that is one fenced block, or the reply itself.</summary>
    public static string Unfence(string reply)
    {
        var trimmed = reply.Trim();
        return trimmed.StartsWith("```", StringComparison.Ordinal) && AiCodeBlocks.Parse(trimmed) is [var only] ? only.Code : reply;
    }

    [GeneratedRegex(@"^diff --git a/(?<path>\S+) b/\S+", RegexOptions.Multiline)]
    private static partial Regex DiffHeader();

    [GeneratedRegex(@"^\s*\d+ \| ?")]
    private static partial Regex LineNumber();
}
