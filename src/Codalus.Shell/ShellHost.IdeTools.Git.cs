using System.Globalization;
using System.Text;
using System.Text.Json;
using Codalus.Workbench.Ai.Ide;
using Codalus.Workbench.Ai.Mcp;
using Codalus.Workbench.Git;
using SolutionPaths = Codalus.Workbench.Projects.SolutionService;
using TextFiles = Codalus.Shell.Editor.TextFiles;

namespace Codalus.Shell;

/// <summary>
/// M60's tools for agents: the shell's side of <see cref="IdeGitTools"/>. Every tool goes through the solution's
/// <see cref="GitService"/>, the one Git Changes, the history and the merge editor use, so what an agent stages, commits,
/// stashes or resolves shows there at once. Nothing here pushes, pulls, fetches, resets, rebases, amends or deletes a
/// branch (D52). Files excluded from AI are left out of every answer (a count says how many) and can't be changed.
/// </summary>
public sealed partial class ShellHost
{
    private const int GitToolDefaultLimit = 30;

    /// <summary>How many lines of a file git_blame shows without a range.</summary>
    private const int GitBlameMaxLines = 500;

    /// <summary>The most of one version of a conflicted file git_conflicts shows.</summary>
    private const int GitConflictVersionLength = 9_000;

    /// <summary>How many untracked files' content git_diff shows when it diffs every change.</summary>
    private const int GitDiffMaxUntracked = 20;

    /// <summary>
    /// The workspace's root as the agent's paths spell it, and the same folder as git reports paths (links resolved):
    /// paths go from one to the other, so the exclusions and the workspace's bounds are checked on the agent's spelling.
    /// </summary>
    private sealed record GitToolScope(string Root, string RealRoot);

    private async Task<IdeToolResult> GitToolAsync(string tool, JsonElement args, string? root, CancellationToken cancellationToken)
    {
        if (root is null)
        {
            throw new IdeToolException("No solution or folder is open.");
        }

        if (Git.Root is null)
        {
            throw new IdeToolException("The workspace isn't in a git repository.");
        }

        var scope = new GitToolScope(root, await Task.Run(() => Codalus.Platform.RealPath.Resolve(root), cancellationToken).ConfigureAwait(true));
        try
        {
            return tool switch
            {
                "git_status" => new(await GitStatusTextAsync(scope, cancellationToken).ConfigureAwait(true)),
                "git_diff" => new(await GitDiffTextAsync(args, scope, cancellationToken).ConfigureAwait(true)),
                "git_log" => new(await GitLogTextAsync(args, scope, cancellationToken).ConfigureAwait(true)),
                "git_file_history" => new(await GitFileHistoryTextAsync(args, scope, cancellationToken).ConfigureAwait(true)),
                "git_blame" => new(await GitBlameTextAsync(args, scope, cancellationToken).ConfigureAwait(true)),
                "git_stage" => new(await GitStageAsync(args, scope, stage: true, cancellationToken).ConfigureAwait(true)),
                "git_unstage" => new(await GitStageAsync(args, scope, stage: false, cancellationToken).ConfigureAwait(true)),
                "git_commit" => new(await GitCommitAsync(args, scope, cancellationToken).ConfigureAwait(true)),
                "git_stash" => new(await GitStashAsync(args, scope, cancellationToken).ConfigureAwait(true)),
                "git_checkout" => new(await GitCheckoutAsync(args, cancellationToken).ConfigureAwait(true)),
                "git_conflicts" => new(await GitConflictsTextAsync(args, scope, cancellationToken).ConfigureAwait(true)),
                "git_resolve" => new(await GitResolveAsync(args, scope, cancellationToken).ConfigureAwait(true)),
                _ => throw new McpException($"There is no tool '{tool}'."),
            };
        }
        catch (GitException ex)
        {
            return IdeToolResult.Error(ex.Message);
        }
    }

    // ------------------------------------------------------------------ status

    private async Task<GitStatus> GitFreshStatusAsync(CancellationToken cancellationToken)
    {
        await Git.RefreshAsync(cancellationToken).ConfigureAwait(true);
        return Git.Status ?? throw new IdeToolException("The workspace isn't in a git repository.");
    }

    /// <summary>A path git reported, as the workspace spells it; null when it is outside the workspace.</summary>
    private static string? GitWorkspacePath(string gitPath, GitToolScope scope)
    {
        var realRoot = scope.RealRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Codalus.Workbench.FileSystem.FileSystems.IsUnder(gitPath, realRoot)
            ? Path.GetFullPath(Path.Combine(scope.Root, Path.GetRelativePath(realRoot, gitPath)))
            : null;
    }

    /// <summary>The files of the status an agent may see (inside the workspace, not excluded), and how many it may not.</summary>
    private async Task<(List<(GitFileStatus File, string Path)> Visible, int Hidden)> GitVisibleAsync(IEnumerable<GitFileStatus> files, GitToolScope scope, CancellationToken cancellationToken)
    {
        var visible = new List<(GitFileStatus, string)>();
        var hidden = 0;
        foreach (var file in files)
        {
            if (GitWorkspacePath(file.Path, scope) is { } path && !await IsExcludedAsync(path, scope.Root, cancellationToken).ConfigureAwait(true))
            {
                visible.Add((file, path));
            }
            else
            {
                hidden++;
            }
        }

        return (visible, hidden);
    }

    private static string GitHiddenNote(int hidden, string what) => hidden == 0
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $"{hidden} {what} excluded from AI (or outside the workspace) {(hidden == 1 ? "isn't" : "aren't")} shown.\n");

    private static string GitChange(char letter) => letter switch
    {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'T' => "type changed",
        '?' => "untracked",
        _ => "changed",
    };

    private static string GitConflictKind(GitFileStatus file) => (file.Index, file.WorkTree) switch
    {
        ('U', 'U') => "both modified",
        ('A', 'A') => "both added",
        ('D', 'D') => "both deleted",
        ('A', 'U') => "added by us",
        ('U', 'A') => "added by them",
        ('D', 'U') => "deleted by us",
        ('U', 'D') => "deleted by them",
        _ => "in conflict",
    };

    private static string? GitOperation(GitStatus status) => status.Operation ?? (status.IsMerging ? "merge" : null);

    private async Task<string> GitStatusTextAsync(GitToolScope scope, CancellationToken cancellationToken)
    {
        var status = await GitFreshStatusAsync(cancellationToken).ConfigureAwait(true);
        var sb = new StringBuilder();
        if (status.Branch is null)
        {
            sb.AppendLine(status.Head is { } head ? $"Detached HEAD at {head[..Math.Min(8, head.Length)]}." : "No commits yet.");
        }
        else
        {
            sb.Append("On branch ").Append(status.Branch);
            if (status.Head is null)
            {
                sb.Append(" (no commits yet)");
            }
            else if (status.Upstream is { } upstream)
            {
                sb.Append(CultureInfo.InvariantCulture, $", tracking {upstream} ({status.Ahead} ahead, {status.Behind} behind)");
            }
            else
            {
                sb.Append(", no upstream");
            }

            sb.AppendLine(".");
        }

        if (GitOperation(status) is { } operation)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"A {operation} is in progress: resolve the conflicts (git_conflicts, git_resolve). A merge is completed with git_commit once none are left; a rebase, cherry-pick or revert is continued or aborted by the user in Git Changes.");
        }

        var (conflicts, hiddenConflicts) = await GitVisibleAsync(status.Conflicts, scope, cancellationToken).ConfigureAwait(true);
        var (staged, hiddenStaged) = await GitVisibleAsync(status.Staged, scope, cancellationToken).ConfigureAwait(true);
        var (unstaged, hiddenUnstaged) = await GitVisibleAsync(status.Unstaged, scope, cancellationToken).ConfigureAwait(true);
        if (conflicts.Count > 0)
        {
            sb.AppendLine("Conflicts:");
            foreach (var (file, path) in conflicts)
            {
                sb.Append("  ").Append(GitConflictKind(file)).Append(": ").AppendLine(Relative(path, scope.Root));
            }
        }

        if (staged.Count > 0)
        {
            sb.AppendLine("Staged:");
            foreach (var (file, path) in staged)
            {
                sb.Append("  ").Append(GitChange(file.Index)).Append(": ").Append(Relative(path, scope.Root));
                if (file.OriginalPath is { } original && GitWorkspacePath(original, scope) is { } from)
                {
                    sb.Append(" (from ").Append(Relative(from, scope.Root)).Append(')');
                }

                sb.AppendLine();
            }
        }

        if (unstaged.Count > 0)
        {
            sb.AppendLine("Not staged:");
            foreach (var (file, path) in unstaged)
            {
                sb.Append("  ").Append(GitChange(file.IsUntracked ? '?' : file.WorkTree)).Append(": ").AppendLine(Relative(path, scope.Root));
            }
        }

        var hidden = hiddenConflicts + hiddenStaged + hiddenUnstaged;
        if (conflicts.Count + staged.Count + unstaged.Count == 0 && hidden == 0)
        {
            sb.AppendLine("Nothing to commit: the working tree is clean.");
        }

        sb.Append(GitHiddenNote(hidden, hidden == 1 ? "change" : "changes"));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ diffs, history, blame

    /// <summary>A revision an agent gave (never an option: git would read <c>--output=…</c> as one).</summary>
    private static string? GitRevision(JsonElement args, string name) => Str(args, name)?.Trim() switch
    {
        null or "" => null,
        ['-', ..] => throw new IdeToolException($"'{Str(args, name)}' isn't a revision."),
        var revision => revision,
    };

    private async Task<string> GitDiffTextAsync(JsonElement args, GitToolScope scope, CancellationToken cancellationToken)
    {
        var path = OptionalPath(args, "path", scope.Root);
        if (path is not null)
        {
            await EnsureReadableAsync(path, scope.Root, cancellationToken).ConfigureAwait(true);
        }

        var git = path is null ? Git : Git.ServiceFor(path);
        var repository = git.Root ?? throw new IdeToolException("The workspace isn't in a git repository.");
        var sb = new StringBuilder();
        IReadOnlyList<FileDiff> diffs;
        if (GitRevision(args, "commit") is { } revision)
        {
            var hash = await git.ResolveAsync(revision, cancellationToken).ConfigureAwait(true) ?? throw new IdeToolException($"There is no commit '{revision}'.");
            if (await git.CommitDetailsAsync(hash, cancellationToken).ConfigureAwait(true) is { } details)
            {
                sb.Append("commit ").AppendLine(hash)
                    .Append("Author: ").Append(details.Commit.Author).Append(" <").Append(details.Commit.AuthorEmail).AppendLine(">")
                    .Append("Date: ").AppendLine(details.Commit.Date.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))
                    .AppendLine();
                foreach (var line in details.Message.Split('\n'))
                {
                    sb.Append("    ").AppendLine(line.TrimEnd('\r'));
                }

                sb.AppendLine();
            }

            diffs = GitParsers.ParseDiff(repository, await git.CommitPatchAsync(hash, cancellationToken).ConfigureAwait(true));
            if (path is not null)
            {
                var real = Codalus.Platform.RealPath.Resolve(path);
                diffs = [.. diffs.Where(d => SolutionPaths.PathComparer.Equals(d.Path, real))];
            }
        }
        else
        {
            var staged = Bool(args, "staged") ?? false;
            if (path is not null)
            {
                diffs = await git.DiffAsync(path, staged, cancellationToken).ConfigureAwait(true) is { } one ? [one] : [];
            }
            else
            {
                await git.RefreshAsync(cancellationToken).ConfigureAwait(true);
                var (_, output, _) = await git.RunAsync(repository, staged ? ["diff", "--cached", "--no-ext-diff", "-M", "--no-color"] : ["diff", "--no-ext-diff", "--no-color"], cancellationToken, readOnly: true).ConfigureAwait(true);
                var all = new List<FileDiff>(GitParsers.ParseDiff(repository, output));
                if (!staged && git.Status is { } status)
                {
                    var (untracked, _) = await GitVisibleAsync(status.Files.Where(f => f.IsUntracked), scope, cancellationToken).ConfigureAwait(true);
                    foreach (var (_, file) in untracked.Take(GitDiffMaxUntracked))
                    {
                        if (await git.DiffAsync(file, staged: false, cancellationToken).ConfigureAwait(true) is { } diff)
                        {
                            all.Add(diff);
                        }
                    }

                    if (untracked.Count > GitDiffMaxUntracked)
                    {
                        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{untracked.Count - GitDiffMaxUntracked} more untracked files aren't shown; ask for one by its path."));
                    }
                }

                diffs = all;
            }
        }

        var hidden = 0;
        var shown = 0;
        foreach (var diff in diffs)
        {
            if (GitWorkspacePath(diff.Path, scope) is not { } file || await IsExcludedAsync(file, scope.Root, cancellationToken).ConfigureAwait(true))
            {
                hidden++;
                continue;
            }

            GitAppendDiff(sb, diff);
            shown++;
        }

        if (shown == 0 && hidden == 0)
        {
            sb.AppendLine(path is null ? "No changes." : $"No changes in {Relative(path, scope.Root)}.");
        }

        sb.Append(GitHiddenNote(hidden, hidden == 1 ? "file's diff" : "files' diffs"));
        return sb.ToString();
    }

    private static void GitAppendDiff(StringBuilder sb, FileDiff diff)
    {
        foreach (var line in diff.Header)
        {
            sb.Append(line).Append('\n');
        }

        foreach (var hunk in diff.Hunks)
        {
            sb.Append(hunk.Header).Append('\n');
            foreach (var line in hunk.Lines)
            {
                sb.Append(line.Kind).Append(line.Text).Append('\n');
            }
        }
    }

    /// <summary><c>HEAD -&gt; refs/heads/main</c>, <c>tag: refs/tags/v1</c> as git log shows them: <c>HEAD -&gt; main</c>, <c>tag: v1</c>.</summary>
    private static string GitRefs(IReadOnlyList<string> refs) => refs.Count == 0
        ? string.Empty
        : " (" + string.Join(", ", refs.Select(r => r.Replace("refs/heads/", "", StringComparison.Ordinal).Replace("refs/remotes/", "", StringComparison.Ordinal).Replace("refs/tags/", "", StringComparison.Ordinal))) + ")";

    private static string GitCommitLines(IReadOnlyList<GitCommit> commits, string none)
    {
        if (commits.Count == 0)
        {
            return none;
        }

        var sb = new StringBuilder();
        foreach (var commit in commits)
        {
            sb.Append(commit.ShortHash).Append(' ').Append(commit.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(' ')
                .Append(commit.Author).Append(": ").Append(commit.Subject).AppendLine(GitRefs(commit.Refs));
        }

        return sb.ToString();
    }

    private async Task<GitService> GitReadyAsync(string? path, CancellationToken cancellationToken)
    {
        var git = path is null ? Git : Git.ServiceFor(path);
        if (git.Status is null)
        {
            await git.RefreshAsync(cancellationToken).ConfigureAwait(true);
        }

        return git;
    }

    private async Task<string> GitLogTextAsync(JsonElement args, GitToolScope scope, CancellationToken cancellationToken)
    {
        var path = OptionalPath(args, "path", scope.Root);
        if (path is not null)
        {
            await EnsureReadableAsync(path, scope.Root, cancellationToken).ConfigureAwait(true);
        }

        var git = await GitReadyAsync(path, cancellationToken).ConfigureAwait(true);
        var limit = Math.Clamp(Int(args, "limit") ?? GitToolDefaultLimit, 1, 200);
        var commits = await git.LogAsync(new GitLogQuery { Path = path, Revision = GitRevision(args, "branch") }, 0, limit, cancellationToken).ConfigureAwait(true);
        return GitCommitLines(commits, git.Status?.Head is null ? "No commits yet." : "No commits.");
    }

    private async Task<string> GitFileHistoryTextAsync(JsonElement args, GitToolScope scope, CancellationToken cancellationToken)
    {
        var path = RequiredPath(args, "path", scope.Root);
        await EnsureReadableAsync(path, scope.Root, cancellationToken).ConfigureAwait(true);
        var git = await GitReadyAsync(path, cancellationToken).ConfigureAwait(true);
        var limit = Math.Clamp(Int(args, "limit") ?? GitToolDefaultLimit, 1, 200);
        if (git.Status?.Head is null)
        {
            return "No commits yet.";
        }

        if (Int(args, "startLine") is { } start)
        {
            var end = Int(args, "endLine") ?? start;
            if (start < 1 || end < start)
            {
                throw new IdeToolException("startLine is 1 or more, and endLine isn't before it.");
            }

            var commits = await git.LineHistoryAsync(path, start, end, cancellationToken).ConfigureAwait(true);
            return GitCommitLines([.. commits.Take(limit)], "No commits changed those lines.");
        }

        return GitCommitLines(await git.LogAsync(new GitLogQuery { Path = path }, 0, limit, cancellationToken).ConfigureAwait(true), "No commits changed that file.");
    }

    private async Task<string> GitBlameTextAsync(JsonElement args, GitToolScope scope, CancellationToken cancellationToken)
    {
        var path = RequiredPath(args, "path", scope.Root);
        await EnsureReadableAsync(path, scope.Root, cancellationToken).ConfigureAwait(true);
        if (!File.Exists(path))
        {
            throw new IdeToolException($"There is no file '{Relative(path, scope.Root)}'.");
        }

        var git = await GitReadyAsync(path, cancellationToken).ConfigureAwait(true);
        var ranges = await git.BlameAsync(path, null, cancellationToken).ConfigureAwait(true);
        var (text, _) = await TextFiles.ReadAsync(path, cancellationToken).ConfigureAwait(true);
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        var first = Math.Max(1, Int(args, "startLine") ?? 1);
        var last = Math.Min(count, Int(args, "endLine") ?? (Int(args, "startLine") is null ? Math.Min(count, GitBlameMaxLines) : count));
        if (first > last)
        {
            return count == 0 ? "The file is empty." : string.Create(CultureInfo.InvariantCulture, $"The file has {count} lines.");
        }

        var sb = new StringBuilder();
        foreach (var range in ranges)
        {
            var from = Math.Max(first, range.StartLine);
            var to = Math.Min(last, range.StartLine + range.LineCount - 1);
            if (from > to)
            {
                continue;
            }

            sb.AppendLine(range.IsUncommitted
                ? "Not committed yet:"
                : $"{range.Commit[..Math.Min(8, range.Commit.Length)]} {range.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} {range.Author}: {range.Summary}");
            for (var line = from; line <= to; line++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  {line}: ").AppendLine(lines[line - 1]);
            }
        }

        if (Int(args, "startLine") is null && Int(args, "endLine") is null && count > last)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"(lines 1–{last} of {count}; give startLine and endLine for the rest)"));
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ staging and commits

    private async Task GitEnsureChangeableAsync(string path, string root, CancellationToken cancellationToken)
    {
        if (await IsExcludedAsync(path, root, cancellationToken).ConfigureAwait(true))
        {
            throw new IdeToolException($"'{Relative(path, root)}' is excluded from AI: the tools can't change it.");
        }
    }

    private static List<string> GitStrings(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var value))
        {
            return [];
        }

        return value.ValueKind switch
        {
            JsonValueKind.Array => [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).Where(s => s.Length > 0)],
            JsonValueKind.String when value.GetString() is { Length: > 0 } one => [one],
            _ => [],
        };
    }

    /// <summary>
    /// What git_stage or git_unstage acts on: <c>all</c> (every change it can, those excluded counted), or the paths given,
    /// a folder standing for the changes in it. A file named that is excluded, or in conflict, is refused.
    /// </summary>
    private async Task<(List<string> Paths, int Hidden)> GitStageTargetsAsync(JsonElement args, GitToolScope scope, bool stage, GitStatus status, CancellationToken cancellationToken)
    {
        var candidates = stage ? status.Unstaged : status.Staged;
        var (visible, hidden) = await GitVisibleAsync(candidates, scope, cancellationToken).ConfigureAwait(true);
        if (Bool(args, "all") == true)
        {
            return ([.. visible.Select(v => v.Path)], hidden);
        }

        var given = GitStrings(args, "paths");
        if (given.Count == 0)
        {
            throw new IdeToolException("Give paths, or all: true.");
        }

        var conflicts = status.Conflicts.Select(f => GitWorkspacePath(f.Path, scope)).OfType<string>().ToHashSet(SolutionPaths.PathComparer);
        var targets = new List<string>();
        foreach (var name in given)
        {
            var path = WorkspacePath(name, scope.Root);
            if (Directory.Exists(path))
            {
                var folder = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                targets.AddRange(visible.Select(v => v.Path).Where(p => p.StartsWith(folder, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)));
                continue;
            }

            await GitEnsureChangeableAsync(path, scope.Root, cancellationToken).ConfigureAwait(true);
            if (conflicts.Contains(path))
            {
                throw new IdeToolException($"'{Relative(path, scope.Root)}' is in conflict: resolve it with git_resolve.");
            }

            targets.Add(path);
        }

        return ([.. targets.Distinct(SolutionPaths.PathComparer)], 0);
    }

    private async Task<string> GitStageAsync(JsonElement args, GitToolScope scope, bool stage, CancellationToken cancellationToken)
    {
        var status = await GitFreshStatusAsync(cancellationToken).ConfigureAwait(true);
        var (paths, hidden) = await GitStageTargetsAsync(args, scope, stage, status, cancellationToken).ConfigureAwait(true);
        if (paths.Count == 0)
        {
            return (stage ? "Nothing to stage.\n" : "Nothing to unstage.\n") + GitHiddenNote(hidden, hidden == 1 ? "change" : "changes");
        }

        foreach (var (service, group) in Git.GroupByRepository(paths))
        {
            await (stage ? service.StageAsync(group, cancellationToken) : service.UnstageAsync(group, cancellationToken)).ConfigureAwait(true);
        }

        var sb = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{(stage ? "Staged" : "Unstaged")} {paths.Count} file(s): "))
            .AppendLine(string.Join(", ", paths.Take(50).Select(p => Relative(p, scope.Root))) + (paths.Count > 50 ? ", …" : ""));
        sb.Append(GitHiddenNote(hidden, hidden == 1 ? "change" : "changes"));
        if (hidden > 0)
        {
            sb.AppendLine(stage ? "Those stay unstaged: the tools don't stage files excluded from AI." : "Those stay staged: the tools don't unstage files excluded from AI.");
        }

        return sb.ToString();
    }

    private async Task<string> GitCommitAsync(JsonElement args, GitToolScope scope, CancellationToken cancellationToken)
    {
        var message = Str(args, "message")?.Trim() is { Length: > 0 } text ? text : throw new IdeToolException("A commit message is needed.");
        var status = await GitFreshStatusAsync(cancellationToken).ConfigureAwait(true);
        if (status.Conflicts.Any())
        {
            throw new IdeToolException("Files are in conflict: resolve them first (git_conflicts, git_resolve).");
        }

        // What is staged must be the agent's to commit: nothing excluded from AI, nothing outside the workspace.
        var (_, hiddenStaged) = await GitVisibleAsync(status.Staged, scope, cancellationToken).ConfigureAwait(true);
        if (hiddenStaged > 0)
        {
            throw new IdeToolException(string.Create(CultureInfo.InvariantCulture,
                $"{hiddenStaged} staged file(s) are excluded from AI or outside the workspace, and the tools don't commit them. Ask the user to commit, or to unstage those files in Git Changes."));
        }

        var left = 0;
        if (Bool(args, "all") == true)
        {
            var (unstaged, hidden) = await GitVisibleAsync(status.Unstaged, scope, cancellationToken).ConfigureAwait(true);
            left = hidden;
            if (unstaged.Count > 0)
            {
                foreach (var (service, group) in Git.GroupByRepository([.. unstaged.Select(u => u.Path)]))
                {
                    await service.StageAsync(group, cancellationToken).ConfigureAwait(true);
                }
            }

            status = Git.Status ?? status;
        }

        var count = status.Staged.Count();
        if (count == 0 && GitOperation(status) is null)
        {
            throw new IdeToolException("Nothing is staged: stage files with git_stage, or pass all: true.");
        }

        await Git.CommitAsync(message, amend: false, all: false, cancellationToken).ConfigureAwait(true);
        var head = await Git.ResolveAsync("HEAD", cancellationToken).ConfigureAwait(true) ?? "";
        var subject = message.Split('\n')[0].Trim();
        var sb = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"Committed {head[..Math.Min(8, head.Length)]} on {Git.Status?.Branch ?? "a detached HEAD"}: {subject} ({count} file(s)). It is not pushed: the user pushes."));
        sb.AppendLine();
        if (left > 0)
        {
            sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{left} change(s) excluded from AI or outside the workspace stay uncommitted."));
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ stashes and branches

    private async Task<string> GitStashAsync(JsonElement args, GitToolScope scope, CancellationToken cancellationToken)
    {
        switch (Str(args, "action") ?? "list")
        {
            case "list":
            {
                var stashes = await Git.StashesAsync(cancellationToken).ConfigureAwait(true);
                return stashes.Count == 0 ? "No stashes." : string.Join('\n', stashes.Select(s => s.Name + ": " + s.Message)) + "\n";
            }

            case "push":
            {
                var status = await GitFreshStatusAsync(cancellationToken).ConfigureAwait(true);
                if (status.Conflicts.Any())
                {
                    throw new IdeToolException("Files are in conflict: resolve them first (git_conflicts, git_resolve).");
                }

                var untracked = Bool(args, "includeUntracked") == true;
                var (changes, hidden) = await GitVisibleAsync(status.Files.Where(f => untracked || !f.IsUntracked), scope, cancellationToken).ConfigureAwait(true);
                if (changes.Count == 0)
                {
                    return "Nothing to stash.\n" + GitHiddenNote(hidden, hidden == 1 ? "change" : "changes");
                }

                // With changes the tools may not touch, only the others go: a partial stash.
                await Git.StashAsync(Str(args, "message")?.Trim(), untracked, hidden > 0 ? [.. changes.Select(c => c.Path)] : null, cancellationToken).ConfigureAwait(true);
                var stashes = await Git.StashesAsync(cancellationToken).ConfigureAwait(true);
                var stash = stashes.Count > 0 ? stashes[0] : null;
                return string.Create(CultureInfo.InvariantCulture, $"Stashed {changes.Count} file(s) as {stash?.Name ?? "stash@{0}"}: {stash?.Message}.\n")
                    + (hidden > 0 ? GitHiddenNote(hidden, hidden == 1 ? "change" : "changes") + "Those stay in the working tree.\n" : "");
            }

            case "pop":
            {
                var index = Int(args, "index") ?? 0;
                var stash = (await Git.StashesAsync(cancellationToken).ConfigureAwait(true)).FirstOrDefault(s => s.Index == index)
                    ?? throw new IdeToolException(string.Create(CultureInfo.InvariantCulture, $"There is no stash {index}; git_stash with action list lists them."));
                foreach (var file in await GitStashFilesAsync(stash, cancellationToken).ConfigureAwait(true))
                {
                    if (GitWorkspacePath(file, scope) is not { } path || await IsExcludedAsync(path, scope.Root, cancellationToken).ConfigureAwait(true))
                    {
                        throw new IdeToolException($"{stash.Name} changes files excluded from AI or outside the workspace, and the tools don't change them. Ask the user to pop it.");
                    }
                }

                await Git.ApplyStashAsync(stash, pop: true, cancellationToken).ConfigureAwait(true);
                return $"Popped {stash.Name} ({stash.Message}) into the working tree.";
            }

            default:
                throw new IdeToolException("action is push, pop or list.");
        }
    }

    /// <summary>The files a stash changes (its tracked changes and its untracked files), as full paths in git's spelling.</summary>
    private async Task<IReadOnlyList<string>> GitStashFilesAsync(GitStash stash, CancellationToken cancellationToken)
    {
        var root = Git.Root!;
        var (_, tracked, _) = await Git.RunAsync(root, ["stash", "show", "--name-only", "-z", stash.Name], cancellationToken, readOnly: true).ConfigureAwait(true);
        var (code, untracked, _) = await Git.RunAsync(root, ["ls-tree", "-r", "-z", "--name-only", stash.Name + "^3"], cancellationToken, check: false, readOnly: true).ConfigureAwait(true);
        return [.. tracked.Split('\0', StringSplitOptions.RemoveEmptyEntries).Concat(code == 0 ? untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries) : [])
            .Select(relative => GitParsers.Full(root, relative.Trim('\n')))];
    }

    private async Task<string> GitCheckoutAsync(JsonElement args, CancellationToken cancellationToken)
    {
        var name = Str(args, "branch")?.Trim() is { Length: > 0 } text ? text : throw new IdeToolException("A branch is needed.");
        if (name.StartsWith('-'))
        {
            throw new IdeToolException($"'{name}' isn't a branch name.");
        }

        var status = await GitFreshStatusAsync(cancellationToken).ConfigureAwait(true);
        var branches = await Git.BranchDetailsAsync(cancellationToken).ConfigureAwait(true);
        if (Bool(args, "create") == true)
        {
            if (GitRefNames.BranchNameError(name) is { } error)
            {
                throw new IdeToolException(error);
            }

            if (branches.Any(b => !b.IsRemote && b.Name == name))
            {
                throw new IdeToolException($"A branch named '{name}' exists: check it out without create.");
            }

            if (GitOperation(status) is { } operation)
            {
                throw new IdeToolException($"A {operation} is in progress: git can't switch branches until it is finished.");
            }

            await Git.CreateBranchAsync(name, switchTo: true, cancellationToken).ConfigureAwait(true);
            return $"Created the branch {name} from {status.Branch ?? "HEAD"} and checked it out.";
        }

        if (FindBranch(branches, name) is not { } branch)
        {
            var locals = branches.Where(b => !b.IsRemote).Select(b => b.Name).Take(30).ToList();
            throw new IdeToolException($"There is no branch named '{name}'. Local branches: {(locals.Count == 0 ? "none" : string.Join(", ", locals))}. Pass create: true to make it.");
        }

        var target = branch.IsRemote && branches.FirstOrDefault(b => !b.IsRemote && b.Name == branch.ShortName) is { } twin ? twin : branch;
        if (target is { IsRemote: false, IsCurrent: true })
        {
            return $"{target.Name} is already checked out.";
        }

        if (target.WorktreePath is { } elsewhere)
        {
            throw new IdeToolException($"{target.Name} is checked out in another worktree ({elsewhere}), so it can't be checked out here too.");
        }

        await Git.SwitchAsync(new GitBranch(target.Name, target.IsRemote, IsCurrent: false, target.Upstream, target.Commit), cancellationToken).ConfigureAwait(true);
        return $"Checked out {Git.Status?.Branch ?? target.ShortName}" + (target.IsRemote ? $", which tracks {target.Name}." : ".");
    }

    // ------------------------------------------------------------------ conflicts

    private async Task<string> GitConflictsTextAsync(JsonElement args, GitToolScope scope, CancellationToken cancellationToken)
    {
        var status = await GitFreshStatusAsync(cancellationToken).ConfigureAwait(true);
        var operation = GitOperation(status);
        if (OptionalPath(args, "path", scope.Root) is { } path)
        {
            await EnsureReadableAsync(path, scope.Root, cancellationToken).ConfigureAwait(true);
            var file = GitConflicted(path, status, scope);
            var (@base, ours, theirs) = await Git.ServiceFor(path).ConflictVersionsAsync(path, cancellationToken).ConfigureAwait(true);
            var sb = new StringBuilder().Append(Relative(path, scope.Root)).Append(": ").Append(GitConflictKind(file)).AppendLine(operation is null ? "." : $", during a {operation}.");
            sb.AppendLine("The file in the working tree has the conflict markers. Resolve it with git_resolve.");
            void Version(string label, string? text)
            {
                sb.AppendLine().Append("===== ").Append(label).AppendLine(" =====");
                sb.AppendLine(text is null ? "(none: this side deleted the file or didn't have it)"
                    : text.Length > GitConflictVersionLength ? text[..GitConflictVersionLength] + "\n… (cut)" : text);
            }

            Version("base (the common ancestor)", @base);
            Version(operation == "rebase" ? "ours (the branch being rebased onto)" : "ours (the current branch)", ours);
            Version(operation == "rebase" ? "theirs (the commit being replayed)" : "theirs (what comes in)", theirs);
            return sb.ToString();
        }

        var (conflicts, hidden) = await GitVisibleAsync(status.Conflicts, scope, cancellationToken).ConfigureAwait(true);
        var list = new StringBuilder();
        if (operation is not null)
        {
            list.AppendLine($"A {operation} is in progress.");
        }

        if (conflicts.Count == 0 && hidden == 0)
        {
            list.AppendLine(operation is null ? "No conflicts." : "No conflicts are left." + (operation == "merge" ? " git_commit completes the merge." : " The user continues it in Git Changes."));
        }

        foreach (var (file, conflicted) in conflicts)
        {
            list.Append("  ").Append(GitConflictKind(file)).Append(": ").AppendLine(Relative(conflicted, scope.Root));
        }

        list.Append(GitHiddenNote(hidden, hidden == 1 ? "conflicted file" : "conflicted files"));
        return list.ToString();
    }

    private static GitFileStatus GitConflicted(string path, GitStatus status, GitToolScope scope) =>
        status.Conflicts.FirstOrDefault(f => GitWorkspacePath(f.Path, scope) is { } p && SolutionPaths.PathComparer.Equals(p, path))
            ?? throw new IdeToolException($"'{Relative(path, scope.Root)}' isn't in conflict.");

    private async Task<string> GitResolveAsync(JsonElement args, GitToolScope scope, CancellationToken cancellationToken)
    {
        var path = RequiredPath(args, "path", scope.Root);
        await GitEnsureChangeableAsync(path, scope.Root, cancellationToken).ConfigureAwait(true);
        var status = await GitFreshStatusAsync(cancellationToken).ConfigureAwait(true);
        GitConflicted(path, status, scope);
        var git = Git.ServiceFor(path);
        var take = Str(args, "take");
        switch (take)
        {
            case "ours" or "theirs":
                await git.ResolveWithAsync(path, take == "ours", cancellationToken).ConfigureAwait(true);
                break;
            case "both":
            {
                var (text, format) = await TextFiles.ReadAsync(path, cancellationToken).ConfigureAwait(true);
                var both = GitTakeBothSides(text) ?? throw new IdeToolException($"'{Relative(path, scope.Root)}' has no complete conflict markers: give its content (take: content).");
                await git.ResolveAsync(path, both, format.Encoding, cancellationToken).ConfigureAwait(true);
                break;
            }

            case "content":
            {
                var content = Str(args, "content") ?? throw new IdeToolException("take content needs the content.");
                if (content.ReplaceLineEndings("\n").Split('\n').Any(l => l.StartsWith("<<<<<<<", StringComparison.Ordinal) || l.StartsWith(">>>>>>>", StringComparison.Ordinal)))
                {
                    throw new IdeToolException("The content still has conflict markers.");
                }

                var format = File.Exists(path) ? (await TextFiles.ReadAsync(path, cancellationToken).ConfigureAwait(true)).Format : null;
                await git.ResolveAsync(path, format is null ? content : content.ReplaceLineEndings(format.LineEnding), format?.Encoding, cancellationToken).ConfigureAwait(true);
                break;
            }

            default:
                throw new IdeToolException("take is ours, theirs, both or content.");
        }

        var left = (await GitVisibleAsync(git.Status?.Conflicts ?? [], scope, cancellationToken).ConfigureAwait(true)).Visible.Count;
        return $"Resolved {Relative(path, scope.Root)} ({take}) and staged it. "
            + (left == 0 ? "No conflicts are left." : string.Create(CultureInfo.InvariantCulture, $"{left} file(s) are still in conflict."));
    }

    /// <summary>
    /// The text with each conflict replaced by its ours side, then its theirs side (a diff3 base section dropped); null when
    /// it has no conflict or one isn't closed.
    /// </summary>
    internal static string? GitTakeBothSides(string text)
    {
        var sb = new StringBuilder(text.Length);
        var state = 0; // 0 outside a conflict, 1 ours, 2 base, 3 theirs
        var found = false;
        for (var i = 0; i < text.Length;)
        {
            var end = text.IndexOf('\n', i);
            var next = end < 0 ? text.Length : end + 1;
            var line = text.AsSpan(i, next - i);
            var content = line.TrimEnd("\r\n");
            if (state == 0 && content.StartsWith("<<<<<<<", StringComparison.Ordinal))
            {
                state = 1;
                found = true;
            }
            else if (state == 1 && content.StartsWith("|||||||", StringComparison.Ordinal))
            {
                state = 2;
            }
            else if (state is 1 or 2 && content.Equals("=======", StringComparison.Ordinal))
            {
                state = 3;
            }
            else if (state == 3 && content.StartsWith(">>>>>>>", StringComparison.Ordinal))
            {
                state = 0;
            }
            else if (state != 2)
            {
                sb.Append(line);
            }

            i = next;
        }

        return found && state == 0 ? sb.ToString() : null;
    }
}
