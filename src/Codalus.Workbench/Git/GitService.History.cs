using System.Globalization;

namespace Codalus.Workbench.Git;

/// <summary>History (M13): pages of the log with filters, a commit's details, file and line history, revisions of a file.</summary>
public sealed partial class GitService
{
    private const string FieldSeparator = "\x1f";
    private const string LogFormat = "%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s";

    /// <summary>A page of the history, newest first, in topological order (see <see cref="GitGraph"/>).</summary>
    public async Task<IReadOnlyList<GitCommit>> LogAsync(GitLogQuery query, int skip, int count, CancellationToken cancellationToken = default)
    {
        if (Status?.Head is null)
        {
            return [];
        }

        var arguments = new List<string>
        {
            "log", "-z", "--topo-order", "--format=" + LogFormat,
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
        var (_, names, _) = await GitAsync(["show", "-z", "--format=", "--name-status", "-M", "--first-parent", "-m", hash], cancellationToken, readOnly: true).ConfigureAwait(false);
        var files = new List<(char, string, string?)>();
        var parts = names.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var status = parts[i].Trim('\n');
            if (status.Length == 0 || i + 1 >= parts.Length)
            {
                continue;
            }

            if (status[0] is 'R' or 'C' && i + 2 < parts.Length)
            {
                files.Add((status[0], Path.Combine(Root!, parts[i + 2]), Path.Combine(Root!, parts[i + 1])));
                i += 2;
            }
            else
            {
                files.Add((status[0], Path.Combine(Root!, parts[i + 1]), null));
                i++;
            }
        }

        return new GitCommitDetails(commit, message.TrimEnd(), [.. files.Select(f => (f.Item1, f.Item2.Replace('/', Path.DirectorySeparatorChar), f.Item3?.Replace('/', Path.DirectorySeparatorChar)))]);
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
