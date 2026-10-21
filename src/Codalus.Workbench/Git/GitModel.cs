using System.Globalization;
using System.Text;

namespace Codalus.Workbench.Git;

/// <summary>A git command failed: its stderr (or stdout), meant for the user.</summary>
public sealed class GitException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// A changed path. <see cref="Index"/> and <see cref="WorkTree"/> are porcelain status letters (<c>.</c> unchanged,
/// <c>M</c>, <c>A</c>, <c>D</c>, <c>R</c>, <c>C</c>, <c>T</c>, <c>U</c> unmerged); untracked files have <c>?</c> in both.
/// </summary>
public sealed record GitFileStatus(string Path, string RelativePath, char Index, char WorkTree, string? OriginalPath = null)
{
    public bool IsUntracked => Index == '?';

    public bool IsConflict => Index == 'U' || WorkTree == 'U' || (Index == 'A' && WorkTree == 'A') || (Index == 'D' && WorkTree == 'D');

    public bool IsStaged => !IsUntracked && !IsConflict && Index != '.';

    public bool IsUnstaged => IsUntracked || (!IsConflict && WorkTree != '.');
}

/// <summary>The repository's state from <c>git status --porcelain=v2 --branch</c>.</summary>
public sealed record GitStatus(
    string Root,
    string? Branch,
    string? Head,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<GitFileStatus> Files,
    bool IsMerging)
{
    /// <summary>
    /// The operation in progress (M13): <c>merge</c>, <c>rebase</c>, <c>cherry-pick</c> or <c>revert</c>; null when none.
    /// Conflicts stop it until they're resolved and it is continued, or it is aborted.
    /// </summary>
    public string? Operation { get; init; }

    public bool IsDetached => Branch is null;

    public IEnumerable<GitFileStatus> Staged => Files.Where(f => f.IsStaged);

    public IEnumerable<GitFileStatus> Unstaged => Files.Where(f => f.IsUnstaged);

    public IEnumerable<GitFileStatus> Conflicts => Files.Where(f => f.IsConflict);
}

public sealed record GitBranch(string Name, bool IsRemote, bool IsCurrent, string? Upstream, string Commit);

/// <summary>What <see cref="GitService.UpdateBranchAsync"/> did to a local branch.</summary>
public enum GitBranchUpdateOutcome
{
    /// <summary>Fast-forwarded to its upstream by <see cref="GitBranchUpdate.Behind"/> commits.</summary>
    Updated,

    /// <summary>It and its upstream are at the same commit: nothing changed.</summary>
    UpToDate,

    /// <summary>It has <see cref="GitBranchUpdate.Ahead"/> commits its upstream doesn't and nothing new from it: nothing to update (Push sends them).</summary>
    Ahead,

    /// <summary>It and its upstream both have commits the other doesn't (<see cref="GitBranchUpdate.Ahead"/>, <see cref="GitBranchUpdate.Behind"/>), so it can't be fast-forwarded: nothing changed.</summary>
    Diverged,

    /// <summary>It has no upstream branch to update from.</summary>
    NoUpstream,

    /// <summary>
    /// It's checked out in another worktree (<see cref="GitBranchUpdate.WorktreePath"/>), where a pull updates it, or a rebase or
    /// bisect there is working on it (<see cref="GitBranchUpdate.Operation"/>): git wasn't run.
    /// </summary>
    CheckedOutElsewhere,

    /// <summary>
    /// A merge or rebase is in progress on it in this worktree, or a rebase or bisect is working on it
    /// (<see cref="GitBranchUpdate.Operation"/>: <c>merge</c>, <c>rebase</c> or <c>bisect</c>): git wasn't run.
    /// </summary>
    OperationInProgress,
}

/// <summary>
/// The result of <see cref="GitService.UpdateBranchAsync"/>: <see cref="Upstream"/> is the upstream's short name
/// (<c>origin/main</c>), <see cref="Before"/> and <see cref="After"/> the branch's commit before and after (full ids),
/// <see cref="Ahead"/> and <see cref="Behind"/> the commits it has that the upstream doesn't and the upstream's it
/// didn't have (for <see cref="GitBranchUpdateOutcome.Updated"/>, the commits it gained).
/// </summary>
public sealed record GitBranchUpdate(string Branch, GitBranchUpdateOutcome Outcome)
{
    public string? Upstream { get; init; }

    public string? Before { get; init; }

    public string? After { get; init; }

    /// <summary>It's the branch checked out in this worktree.</summary>
    public bool IsCurrent { get; init; }

    public int Ahead { get; init; }

    public int Behind { get; init; }

    /// <summary>Where it's checked out, for <see cref="GitBranchUpdateOutcome.CheckedOutElsewhere"/> (with the OS's separators).</summary>
    public string? WorktreePath { get; init; }

    /// <summary>The operation that holds it (<c>merge</c>, <c>rebase</c>, <c>bisect</c>), or null.</summary>
    public string? Operation { get; init; }
}

/// <summary>
/// A branch that a rebase or bisect is working on (<see cref="Operation"/>): HEAD is detached while they run, so git
/// lists no branch as checked out, yet it counts this one as checked out where they run. <see cref="WorktreePath"/> is
/// that worktree, <see cref="IsThisWorktree"/> whether it's the repository's own.
/// </summary>
public sealed record GitBranchOperation(string Branch, string Operation, string? WorktreePath, bool IsThisWorktree);

/// <summary>One line of a hunk: <see cref="Kind"/> is <c>' '</c> (context), <c>'+'</c>, <c>'-'</c> or <c>'\\'</c> (no newline at end of file).</summary>
public sealed record DiffLine(char Kind, string Text);

/// <summary>A hunk of a unified diff (1-based starts, as in <c>@@ -a,b +c,d @@</c>).</summary>
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, string Header, IReadOnlyList<DiffLine> Lines)
{
    public int Added => Lines.Count(l => l.Kind == '+');

    public int Removed => Lines.Count(l => l.Kind == '-');
}

/// <summary>The diff of one file: its header lines (<c>diff --git</c>, <c>index</c>, <c>---</c>, <c>+++</c>…) and hunks.</summary>
public sealed record FileDiff(string Path, IReadOnlyList<string> Header, IReadOnlyList<DiffHunk> Hunks, bool IsBinary)
{
    /// <summary>A patch with only <paramref name="hunk"/>, for <c>git apply</c>.</summary>
    public string PatchFor(DiffHunk hunk)
    {
        var patch = new StringBuilder();
        foreach (var line in Header)
        {
            patch.Append(line).Append('\n');
        }

        patch.Append(hunk.Header).Append('\n');
        foreach (var line in hunk.Lines)
        {
            patch.Append(line.Kind).Append(line.Text).Append('\n');
        }

        return patch.ToString();
    }

    /// <summary>
    /// A patch with only the chosen lines of <paramref name="hunk"/> (indexes into its lines), for per-line staging
    /// (<paramref name="reverse"/> false: applied to the index) or unstaging and discarding (true: applied in reverse).
    /// Lines not chosen stay as they are on the side being changed: an unchosen removal becomes context, an unchosen
    /// addition is left out (reversed, the other way around).
    /// </summary>
    public string PatchForLines(DiffHunk hunk, IReadOnlySet<int> chosen, bool reverse = false)
    {
        var lines = new List<DiffLine>();
        for (var i = 0; i < hunk.Lines.Count; i++)
        {
            var line = hunk.Lines[i];
            var keep = chosen.Contains(i);
            switch (line.Kind)
            {
                case '+' when !keep:
                    if (reverse)
                    {
                        lines.Add(new DiffLine(' ', line.Text));
                    }

                    break;
                case '-' when !keep:
                    if (!reverse)
                    {
                        lines.Add(new DiffLine(' ', line.Text));
                    }

                    break;
                default:
                    lines.Add(line);
                    break;
            }
        }

        var oldCount = lines.Count(l => l.Kind is ' ' or '-');
        var newCount = lines.Count(l => l.Kind is ' ' or '+');
        // Applied forward, the other hunks aren't in the target yet, so the new side starts where the old one does;
        // applied in reverse, git finds the hunk by its new side, which is where it is in the file now.
        var newStart = reverse ? hunk.NewStart : hunk.OldStart;
        var header = $"@@ -{hunk.OldStart.ToString(CultureInfo.InvariantCulture)},{oldCount.ToString(CultureInfo.InvariantCulture)} +{newStart.ToString(CultureInfo.InvariantCulture)},{newCount.ToString(CultureInfo.InvariantCulture)} @@";
        return PatchFor(hunk with { Header = header, Lines = lines, OldCount = oldCount, NewCount = newCount });
    }
}

/// <summary>
/// A commit in the history (M13): its full and short hash, parents, author, dates, refs (<c>HEAD -&gt; main</c>,
/// <c>tag: v1</c>…) and subject. <see cref="Graph"/> places it in the history graph.
/// </summary>
public sealed record GitCommit(string Hash, IReadOnlyList<string> Parents, string Author, string AuthorEmail, DateTimeOffset Date, IReadOnlyList<string> Refs, string Subject)
{
    public string ShortHash => Hash.Length > 8 ? Hash[..8] : Hash;

    public GraphRow? Graph { get; set; }
}

/// <summary>
/// A commit's row in the history graph (M13): its lane (column) and the lines to draw. <see cref="Top"/> runs from the
/// row's top edge at lane <c>From</c> to the commit's dot (lane <c>To</c>, always <see cref="Lane"/>); <see cref="Bottom"/>
/// from the dot to the bottom edge at lane <c>To</c>; <see cref="Passing"/> lanes go straight through.
/// </summary>
public sealed record GraphRow(int Lane, int LaneCount, IReadOnlyList<(int From, int To)> Top, IReadOnlyList<(int From, int To)> Bottom, IReadOnlyList<int> Passing);

/// <summary>A history query: a revision range (null: every branch), and filters.</summary>
public sealed record GitLogQuery
{
    /// <summary>A branch or revision; null with <see cref="AllBranches"/> false means HEAD.</summary>
    public string? Revision { get; init; }

    public bool AllBranches { get; init; }

    public string? Author { get; init; }

    /// <summary>Text in the commit message (case-insensitive).</summary>
    public string? Message { get; init; }

    /// <summary>A file or folder: its history (renames followed for a single file).</summary>
    public string? Path { get; init; }

    public DateTimeOffset? Since { get; init; }

    public DateTimeOffset? Until { get; init; }
}

/// <summary>A commit's full message and changed files (status letter and path, the old path of a rename).</summary>
public sealed record GitCommitDetails(GitCommit Commit, string Message, IReadOnlyList<(char Status, string Path, string? OldPath)> Files);

public sealed record GitStash(int Index, string Name, string Message);

public sealed record GitTag(string Name, string Commit, string? Message);

public sealed record GitRemote(string Name, string FetchUrl, string? PushUrl);

public sealed record GitSubmodule(string Path, string Commit, char State)
{
    /// <summary><c>-</c> not initialized, <c>+</c> checked out at another commit, <c>U</c> conflicts, space: up to date.</summary>
    public bool IsInitialized => State != '-';
}

/// <summary>An entry of an interactive rebase's todo list: <c>pick</c>, <c>reword</c>, <c>squash</c>, <c>fixup</c> or <c>drop</c>.</summary>
public sealed record RebaseStep(string Action, string Hash, string Subject)
{
    /// <summary>The new message, for <c>reword</c>.</summary>
    public string? Message { get; init; }
}

/// <summary>The commit that last changed a range of lines (1-based, inclusive).</summary>
public sealed record BlameRange(int StartLine, int LineCount, string Commit, string Author, DateTimeOffset Date, string Summary)
{
    public bool IsUncommitted => Commit.All(c => c == '0');
}

/// <summary>Parsers for git's machine-readable output.</summary>
public static partial class GitParsers
{
    /// <summary>Parses <c>git status --porcelain=v2 -z --branch</c>.</summary>
    public static GitStatus ParseStatus(string root, string output, bool isMerging)
    {
        string? branch = null, head = null, upstream = null;
        int ahead = 0, behind = 0;
        var files = new List<GitFileStatus>();
        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.Length == 0)
            {
                continue;
            }

            switch (record[0])
            {
                case '#':
                    var parts = record.Split(' ');
                    switch (parts.Length > 1 ? parts[1] : null)
                    {
                        case "branch.oid" when parts.Length > 2:
                            head = parts[2] == "(initial)" ? null : parts[2];
                            break;
                        case "branch.head" when parts.Length > 2:
                            branch = parts[2] == "(detached)" ? null : record["# branch.head ".Length..];
                            break;
                        case "branch.upstream" when parts.Length > 2:
                            upstream = record["# branch.upstream ".Length..];
                            break;
                        case "branch.ab" when parts.Length > 3:
                            ahead = int.Parse(parts[2].TrimStart('+'), CultureInfo.InvariantCulture);
                            behind = int.Parse(parts[3].TrimStart('-'), CultureInfo.InvariantCulture);
                            break;
                    }

                    break;
                case '1':
                {
                    // 1 XY sub mH mI mW hH hI path
                    var fields = record.Split(' ', 9);
                    files.Add(File(root, fields[8], fields[1][0], fields[1][1]));
                    break;
                }

                case '2':
                {
                    // 2 XY sub mH mI mW hH hI Xscore path, then the original path as the next record
                    var fields = record.Split(' ', 10);
                    var original = i + 1 < records.Length ? records[++i] : null;
                    files.Add(File(root, fields[9], fields[1][0], fields[1][1]) with { OriginalPath = original is null ? null : Full(root, original) });
                    break;
                }

                case 'u':
                {
                    // u XY sub m1 m2 m3 mW h1 h2 h3 path
                    var fields = record.Split(' ', 11);
                    files.Add(File(root, fields[10], fields[1][0], fields[1][1]));
                    break;
                }

                case '?':
                    files.Add(File(root, record[2..], '?', '?'));
                    break;
            }
        }

        return new GitStatus(root, branch, head, upstream, ahead, behind, files, isMerging);
    }

    private static GitFileStatus File(string root, string relative, char index, char workTree) =>
        new(Full(root, relative), relative, index, workTree);

    public static string Full(string root, string relative) =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));

    /// <summary>Parses a unified diff (<c>git diff</c>) of one or more files.</summary>
    public static IReadOnlyList<FileDiff> ParseDiff(string root, string output)
    {
        var result = new List<FileDiff>();
        var lines = output.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            if (!lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                i++;
                continue;
            }

            var header = new List<string> { lines[i++] };
            string? path = null;
            var binary = false;
            while (i < lines.Length && !lines[i].StartsWith("@@", StringComparison.Ordinal) && !lines[i].StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var line = lines[i++];
                if (line.Length == 0 && i == lines.Length)
                {
                    break;
                }

                header.Add(line);
                if (line.StartsWith("+++ b/", StringComparison.Ordinal))
                {
                    path = line[6..];
                }
                else if (line.StartsWith("--- a/", StringComparison.Ordinal))
                {
                    path ??= line[6..];
                }
                else if (line.StartsWith("Binary files ", StringComparison.Ordinal))
                {
                    binary = true;
                }
            }

            path ??= header[0].Split(" b/", 2) is [_, var b] ? b : header[0];
            var hunks = new List<DiffHunk>();
            while (i < lines.Length && lines[i].StartsWith("@@", StringComparison.Ordinal))
            {
                var hunkHeader = lines[i++];
                var (oldStart, oldCount, newStart, newCount) = ParseHunkHeader(hunkHeader);
                var hunkLines = new List<DiffLine>();
                while (i < lines.Length && lines[i].Length > 0 && lines[i][0] is ' ' or '+' or '-' or '\\')
                {
                    hunkLines.Add(new DiffLine(lines[i][0], lines[i][1..]));
                    i++;
                }

                hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount, hunkHeader, hunkLines));
            }

            result.Add(new FileDiff(Full(root, path), header, hunks, binary));
        }

        return result;
    }

    /// <summary><c>@@ -a[,b] +c[,d] @@ …</c> → (a, b, c, d); a missing count is 1.</summary>
    public static (int OldStart, int OldCount, int NewStart, int NewCount) ParseHunkHeader(string header)
    {
        var end = header.IndexOf(" @@", 2, StringComparison.Ordinal);
        var ranges = header[3..(end < 0 ? header.Length : end)].Split(' ');
        static (int, int) Range(string text)
        {
            var parts = text[1..].Split(',');
            return (int.Parse(parts[0], CultureInfo.InvariantCulture), parts.Length > 1 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 1);
        }

        var (oldStart, oldCount) = Range(ranges[0]);
        var (newStart, newCount) = Range(ranges[1]);
        return (oldStart, oldCount, newStart, newCount);
    }

    /// <summary>Parses <c>git blame --porcelain</c> into ranges of consecutive lines from the same commit.</summary>
    public static IReadOnlyList<BlameRange> ParseBlame(string output)
    {
        var commits = new Dictionary<string, (string Author, DateTimeOffset Date, string Summary)>(StringComparer.Ordinal);
        var lineCommits = new List<(int Line, string Commit)>();
        string? current = null;
        int currentLine = 0;
        string author = string.Empty, summary = string.Empty;
        long time = 0;
        var tz = TimeSpan.Zero;
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith('\t'))
            {
                if (current is not null)
                {
                    if (!commits.ContainsKey(current))
                    {
                        commits[current] = (author, DateTimeOffset.FromUnixTimeSeconds(time).ToOffset(tz), summary);
                    }

                    lineCommits.Add((currentLine, current));
                }

                continue;
            }

            var parts = line.Split(' ');
            if (parts.Length >= 3 && parts[0].Length == 40 && parts[0].All(Uri.IsHexDigit))
            {
                current = parts[0];
                currentLine = int.Parse(parts[2], CultureInfo.InvariantCulture);
                if (!commits.ContainsKey(current))
                {
                    author = summary = string.Empty;
                    time = 0;
                    tz = TimeSpan.Zero;
                }
            }
            else if (line.StartsWith("author ", StringComparison.Ordinal))
            {
                author = line[7..];
            }
            else if (line.StartsWith("author-time ", StringComparison.Ordinal))
            {
                time = long.Parse(line[12..], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("author-tz ", StringComparison.Ordinal) && line.Length >= 15)
            {
                var sign = line[10] == '-' ? -1 : 1;
                tz = sign * new TimeSpan(int.Parse(line[11..13], CultureInfo.InvariantCulture), int.Parse(line[13..15], CultureInfo.InvariantCulture), 0);
            }
            else if (line.StartsWith("summary ", StringComparison.Ordinal))
            {
                summary = line[8..];
            }
        }

        var ranges = new List<BlameRange>();
        foreach (var (line, commit) in lineCommits.OrderBy(l => l.Line))
        {
            if (ranges.Count > 0 && ranges[^1].Commit == commit && ranges[^1].StartLine + ranges[^1].LineCount == line)
            {
                ranges[^1] = ranges[^1] with { LineCount = ranges[^1].LineCount + 1 };
            }
            else
            {
                var (a, d, s) = commits[commit];
                ranges.Add(new BlameRange(line, 1, commit, a, d, s));
            }
        }

        return ranges;
    }
}
