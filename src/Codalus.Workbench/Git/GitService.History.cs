using System.Globalization;

namespace Codalus.Workbench.Git;

/// <summary>History (M13): pages of the log with filters, a commit's details, file and line history, revisions of a file.</summary>
public sealed partial class GitService
{
    private const string FieldSeparator = "\x1f";
    private const string LogFormat = "%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s";

    /// <summary>
    /// A page of the history, newest first, in topological order (see <see cref="GitGraph"/>). TODO(issue): each page runs
    /// git again with <c>--skip</c>, which walks (and topologically sorts) every commit before it: one <c>git log</c> streamed
    /// as the list scrolls would cost each page only its own commits (M57 kept the history as it is).
    /// </summary>
    public async Task<IReadOnlyList<GitCommit>> LogAsync(GitLogQuery query, int skip, int count, CancellationToken cancellationToken = default)
    {
        if (Status?.Head is null)
        {
            return [];
        }

        var arguments = new List<string>
        {
            "log", "-z", "--topo-order", "--decorate=full", "--format=" + LogFormat,
            "--skip=" + skip.ToString(CultureInfo.InvariantCulture), "--max-count=" + count.ToString(CultureInfo.InvariantCulture),
        };
        if (query.Author is { Length: > 0 } author)
        {
            arguments.Add("--author=" + author);
            arguments.Add("--regexp-ignore-case");
        }

        if (query.Message is { Length: > 0 } message)
        {
            arguments.Add("--grep=" + message);
            arguments.Add("--regexp-ignore-case");
            arguments.Add("--fixed-strings");
        }

        if (query.Since is { } since)
        {
            arguments.Add("--since=" + since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }

        if (query.Until is { } until)
        {
            arguments.Add("--until=" + until.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        }

        var singleFile = query.Path is { } path && File.Exists(path);
        if (singleFile)
        {
            arguments.Add("--follow");
        }

        if (query.AllBranches)
        {
            arguments.Add("--all");
        }
        else if (query.Revision is { Length: > 0 } revision)
        {
            arguments.Add(revision);
        }

        if (query.Path is { } filter)
        {
            arguments.Add("--");
            arguments.Add(Relative(filter));
        }

        var (_, output, _) = await GitAsync(arguments, cancellationToken, readOnly: true).ConfigureAwait(false);
        return ParseLog(output);
    }

    internal static IReadOnlyList<GitCommit> ParseLog(string output)
    {
        var commits = new List<GitCommit>();
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = record.TrimStart('\n').Split(FieldSeparator);
            if (fields.Length < 7)
            {
                continue;
            }

            commits.Add(new GitCommit(
                fields[0],
                fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
                fields[2],
                fields[3],
                DateTimeOffset.FromUnixTimeSeconds(long.Parse(fields[4], CultureInfo.InvariantCulture)),
                fields[5].Split(", ", StringSplitOptions.RemoveEmptyEntries),
                fields[6]));
        }

        return commits;
    }

    /// <summary>A commit's full message and changed files (against its first parent).</summary>
    public async Task<GitCommitDetails?> CommitDetailsAsync(string hash, CancellationToken cancellationToken = default)
    {
        var (code, header, _) = await RunAsync(Root!, ["show", "-s", "-z", "--format=" + LogFormat, hash], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        if (code != 0 || ParseLog(header) is not [var commit])
        {
            return null;
        }

        var (_, message, _) = await GitAsync(["show", "-s", "--format=%B", hash], cancellationToken, readOnly: true).ConfigureAwait(false);
        var (_, committer, _) = await GitAsync(["show", "-s", "--format=%cn%x1f%ce%x1f%ct", hash], cancellationToken, readOnly: true).ConfigureAwait(false);
        var (_, names, _) = await GitAsync(["show", "-z", "--format=", "--name-status", "-M", "--first-parent", "-m", hash], cancellationToken, readOnly: true).ConfigureAwait(false);
        var fields = committer.Trim().Split(FieldSeparator);
        return new GitCommitDetails(commit, message.TrimEnd(), ParseNameStatus(Root!, names))
        {
            Committer = fields.Length == 3 ? fields[0] : commit.Author,
            CommitterEmail = fields.Length == 3 ? fields[1] : commit.AuthorEmail,
            CommitDate = fields.Length == 3 && long.TryParse(fields[2], CultureInfo.InvariantCulture, out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : commit.Date,
        };
    }

    /// <summary>A commit's patch against its first parent (what Explain gives AI Chat).</summary>
    public async Task<string> CommitPatchAsync(string hash, CancellationToken cancellationToken = default)
    {
        var (_, patch, _) = await GitAsync(["show", "--format=", "--patch", "-M", "--first-parent", "-m", "--no-color", hash], cancellationToken, readOnly: true).ConfigureAwait(false);
        return patch;
    }

    /// <summary>
    /// The commits of <paramref name="query"/>'s revisions (or every branch) whose message or author contains
    /// <paramref name="text"/> (case-insensitive), and the commit it names when it's a commit id or a prefix of one, even
    /// one the revisions don't reach; newest first, at most <paramref name="count"/>.
    /// </summary>
    public async Task<IReadOnlyList<GitCommit>> SearchLogAsync(GitLogQuery query, string text, int count, CancellationToken cancellationToken = default)
    {
        if (Status?.Head is null || text.Trim() is not { Length: > 0 } term)
        {
            return [];
        }

        var found = new Dictionary<string, GitCommit>(StringComparer.Ordinal);
        foreach (var filter in new[] { "--grep=" + term, "--author=" + term })
        {
            var arguments = new List<string>
            {
                "log", "-z", "--decorate=full", "--format=" + LogFormat, "--regexp-ignore-case", "--fixed-strings", filter,
                "--max-count=" + count.ToString(CultureInfo.InvariantCulture),
            };
            if (query.AllBranches)
            {
                arguments.Add("--all");
            }
            else if (query.Revision is { Length: > 0 } revision)
            {
                arguments.Add(revision);
            }

            if (query.Path is { } path)
            {
                arguments.Add("--");
                arguments.Add(Relative(path));
            }

            var (_, output, _) = await GitAsync(arguments, cancellationToken, readOnly: true).ConfigureAwait(false);
            foreach (var commit in ParseLog(output))
            {
                found.TryAdd(commit.Hash, commit);
            }
        }

        if (term.Length is >= 4 and <= 40 && term.All(Uri.IsHexDigit) && await ResolveAsync(term, cancellationToken).ConfigureAwait(false) is { } hash && !found.ContainsKey(hash))
        {
            var (_, output, _) = await GitAsync(["log", "-z", "-1", "--decorate=full", "--format=" + LogFormat, hash], cancellationToken, readOnly: true).ConfigureAwait(false);
            foreach (var commit in ParseLog(output))
            {
                found.TryAdd(commit.Hash, commit);
            }
        }

        return [.. found.Values.OrderByDescending(c => c.Date).Take(count)];
    }

    /// <summary>The commits that changed lines <paramref name="startLine"/>–<paramref name="endLine"/> of a file (1-based).</summary>
    public async Task<IReadOnlyList<GitCommit>> LineHistoryAsync(string path, int startLine, int endLine, CancellationToken cancellationToken = default)
    {
        const string Marker = "\x1eCOMMIT\x1f";
        var range = $"-L{startLine.ToString(CultureInfo.InvariantCulture)},{endLine.ToString(CultureInfo.InvariantCulture)}:{Relative(path)}";
        var (_, output, _) = await GitAsync(["log", range, "--format=" + Marker + LogFormat], cancellationToken, readOnly: true).ConfigureAwait(false);

        // -L prints each commit's diff after it; only the marked lines are commits.
        var commits = output.Split('\n')
            .Where(l => l.StartsWith(Marker, StringComparison.Ordinal))
            .Select(l => l[Marker.Length..] + "\0");
        return ParseLog(string.Concat(commits));
    }

    /// <summary>The revision a blame line or a commit points at, resolved to a full hash; null when it doesn't exist.</summary>
    public async Task<string?> ResolveAsync(string revision, CancellationToken cancellationToken = default)
    {
        var (code, output, _) = await RunAsync(Root!, ["rev-parse", "--verify", "--quiet", revision + "^{commit}"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        return code == 0 ? output.Trim() : null;
    }
}
