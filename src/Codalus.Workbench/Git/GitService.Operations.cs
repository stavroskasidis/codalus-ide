using System.Globalization;
using System.Text;

namespace Codalus.Workbench.Git;

/// <summary>
/// Operations beyond the MVP (M13): operations in progress (continue, abort, skip), conflicts, stash, cherry-pick,
/// revert, reset, rebase (also interactive), tags, remotes, per-line staging, ignoring, submodules, commit templates,
/// and creating or cloning a repository.
/// </summary>
public sealed partial class GitService
{
    /// <summary>The repository's git directory (<c>.git</c>, or a worktree's), after a refresh.</summary>
    public string? GitDirectory { get; private set; }

    internal static string? OperationIn(string gitDirectory) =>
        Directory.Exists(Path.Combine(gitDirectory, "rebase-merge")) || Directory.Exists(Path.Combine(gitDirectory, "rebase-apply")) ? "rebase"
        : File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD")) ? "cherry-pick"
        : File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD")) ? "revert"
        : File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD")) ? "merge"
        : null;

    /// <summary>
    /// Runs git without an editor: continuing a rebase, cherry-pick or revert keeps the prepared messages
    /// (<c>GIT_EDITOR=true</c>), as Continue in an IDE does.
    /// </summary>
    private async Task MutateWithoutEditorAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? environment = null)
    {
        try
        {
            var variables = new Dictionary<string, string>(environment ?? new Dictionary<string, string>()) { ["GIT_EDITOR"] = "true" };
            await RunAsync(Root ?? throw new InvalidOperationException("No git repository is open."), arguments, cancellationToken, environment: variables).ConfigureAwait(false);
        }
        finally
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ the operation in progress

    /// <summary>Continues the merge, rebase, cherry-pick or revert after its conflicts are resolved and staged.</summary>
    public Task ContinueAsync(CancellationToken cancellationToken = default) => Status?.Operation switch
    {
        "merge" => MutateWithoutEditorAsync(["commit", "--no-edit"], cancellationToken),
        "rebase" => MutateWithoutEditorAsync(["rebase", "--continue"], cancellationToken),
        "cherry-pick" => MutateWithoutEditorAsync(["cherry-pick", "--continue"], cancellationToken),
        "revert" => MutateWithoutEditorAsync(["revert", "--continue"], cancellationToken),
        _ => throw new GitException("No merge, rebase, cherry-pick or revert is in progress.", -1),
    };

    /// <summary>Abandons the operation in progress and goes back to where it started.</summary>
    public Task AbortAsync(CancellationToken cancellationToken = default) => Status?.Operation switch
    {
        "merge" => MutateAsync(["merge", "--abort"], cancellationToken),
        "rebase" => MutateAsync(["rebase", "--abort"], cancellationToken),
        "cherry-pick" => MutateAsync(["cherry-pick", "--abort"], cancellationToken),
        "revert" => MutateAsync(["revert", "--abort"], cancellationToken),
        _ => throw new GitException("No merge, rebase, cherry-pick or revert is in progress.", -1),
    };

    /// <summary>Skips the commit that stopped a rebase, cherry-pick or revert.</summary>
    public Task SkipAsync(CancellationToken cancellationToken = default) => Status?.Operation switch
    {
        "rebase" => MutateWithoutEditorAsync(["rebase", "--skip"], cancellationToken),
        "cherry-pick" => MutateWithoutEditorAsync(["cherry-pick", "--skip"], cancellationToken),
        "revert" => MutateWithoutEditorAsync(["revert", "--skip"], cancellationToken),
        _ => throw new GitException("Only a rebase, cherry-pick or revert can skip a commit.", -1),
    };

    // ------------------------------------------------------------------ conflicts

    /// <summary>
    /// The three versions of a conflicted file: the common ancestor (stage 1), ours (stage 2, the current branch) and
    /// theirs (stage 3, what is being merged in). A version is null when that side deleted or didn't have the file.
    /// </summary>
    public async Task<(string? Base, string? Ours, string? Theirs)> ConflictVersionsAsync(string path, CancellationToken cancellationToken = default)
    {
        async Task<string?> Stage(int stage)
        {
            var (code, output, _) = await RunAsync(Root!, ["show", $":{stage.ToString(CultureInfo.InvariantCulture)}:{Relative(path)}"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
            return code == 0 ? output : null;
        }

        return (await Stage(1).ConfigureAwait(false), await Stage(2).ConfigureAwait(false), await Stage(3).ConfigureAwait(false));
    }

    /// <summary>Writes the resolved content (UTF-8) and stages it: the file is no longer in conflict.</summary>
    public Task ResolveAsync(string path, string content, CancellationToken cancellationToken = default) =>
        ResolveAsync(path, content, null, cancellationToken);

    /// <summary>
    /// Writes the resolved content in <paramref name="encoding"/> (the file's own, its byte order mark included; UTF-8
    /// without one when null) and stages it: the file is no longer in conflict.
    /// </summary>
    public async Task ResolveAsync(string path, string content, Encoding? encoding, CancellationToken cancellationToken = default)
    {
        await File.WriteAllTextAsync(path, content, encoding ?? new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await MutateAsync(["add", "--", Relative(path)], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves a conflict by taking one side's whole file (<c>ours</c> or <c>theirs</c>).</summary>
    public async Task ResolveWithAsync(string path, bool ours, CancellationToken cancellationToken = default)
    {
        await GitAsync(["checkout", ours ? "--ours" : "--theirs", "--", Relative(path)], cancellationToken).ConfigureAwait(false);
        await MutateAsync(["add", "--", Relative(path)], cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ stash

    public async Task<IReadOnlyList<GitStash>> StashesAsync(CancellationToken cancellationToken = default)
    {
        var (_, output, _) = await GitAsync(["stash", "list", "--format=%gd%x1f%gs"], cancellationToken, readOnly: true).ConfigureAwait(false);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\x1f'))
            .Where(parts => parts.Length == 2)
            .Select((parts, index) => new GitStash(index, parts[0], parts[1]))];
    }

    /// <summary>
    /// Stashes the changes (only <paramref name="paths"/> when given: a partial stash, its paths resolved off the caller's
    /// thread and given to git on its input), with untracked files optionally.
    /// </summary>
    public async Task StashAsync(string? message, bool includeUntracked, IReadOnlyList<string>? paths = null, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "stash", "push" };
        if (message is { Length: > 0 })
        {
            arguments.Add("--message=" + message);
        }

        if (includeUntracked)
        {
            arguments.Add("--include-untracked");
        }

        string? input = null;
        if (paths is { Count: > 0 })
        {
            arguments.AddRange(s_pathspecsFromInput);
            input = Pathspecs(await RelativeAsync(paths, cancellationToken).ConfigureAwait(false));
        }

        await MutateAsync(arguments, cancellationToken, input).ConfigureAwait(false);
    }

    /// <summary>Applies a stash; <paramref name="pop"/> also drops it when it applied cleanly.</summary>
    public Task ApplyStashAsync(GitStash stash, bool pop, CancellationToken cancellationToken = default) =>
        MutateAsync(["stash", pop ? "pop" : "apply", "--index", stash.Name], cancellationToken);

    public Task DropStashAsync(GitStash stash, CancellationToken cancellationToken = default) =>
        MutateAsync(["stash", "drop", stash.Name], cancellationToken);

    /// <summary>Drops every stash.</summary>
    public Task ClearStashesAsync(CancellationToken cancellationToken = default) => MutateAsync(["stash", "clear"], cancellationToken);

    // ------------------------------------------------------------------ commits

    /// <summary>Applies a commit's changes onto the current branch. Conflicts stop it (see <see cref="GitStatus.Operation"/>).</summary>
    public Task CherryPickAsync(string hash, CancellationToken cancellationToken = default) =>
        MutateWithoutEditorAsync(["cherry-pick", hash], cancellationToken);

    /// <summary>Commits the inverse of a commit.</summary>
    public Task RevertAsync(string hash, CancellationToken cancellationToken = default) =>
        MutateWithoutEditorAsync(["revert", "--no-edit", hash], cancellationToken);

    /// <summary>Moves the branch to <paramref name="hash"/>: <c>soft</c> keeps changes staged, <c>mixed</c> unstaged, <c>hard</c> discards them.</summary>
    public Task ResetAsync(string hash, string mode, CancellationToken cancellationToken = default) =>
        mode is "soft" or "mixed" or "hard"
            ? MutateAsync(["reset", "--" + mode, hash], cancellationToken)
            : throw new ArgumentException("The mode must be soft, mixed or hard.", nameof(mode));

    /// <summary>Checks out a commit without a branch (detached HEAD).</summary>
    public Task CheckoutAsync(string hash, CancellationToken cancellationToken = default) =>
        MutateAsync(["switch", "--detach", hash], cancellationToken);

    /// <summary>Creates a branch at a commit.</summary>
    public Task CreateBranchAtAsync(string name, string hash, bool switchTo, CancellationToken cancellationToken = default) =>
        switchTo ? MutateAsync(["switch", "-c", name, hash], cancellationToken) : MutateAsync(["branch", name, hash], cancellationToken);

    /// <summary>Rebases the current branch onto <paramref name="onto"/>. Conflicts stop it.</summary>
    public Task RebaseAsync(string onto, CancellationToken cancellationToken = default) =>
        MutateWithoutEditorAsync(["rebase", onto], cancellationToken);

    /// <summary>The commits an interactive rebase onto <paramref name="onto"/> would replay, oldest first (all <c>pick</c>).</summary>
    public async Task<IReadOnlyList<RebaseStep>> RebaseTodoAsync(string onto, CancellationToken cancellationToken = default)
    {
        var (_, output, _) = await GitAsync(["log", "--reverse", "--no-merges", "--format=%H%x1f%s", onto + "..HEAD"], cancellationToken, readOnly: true).ConfigureAwait(false);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\x1f'))
            .Where(parts => parts.Length == 2)
            .Select(parts => new RebaseStep("pick", parts[0], parts[1]))];
    }

    /// <summary>
    /// Runs an interactive rebase with the given todo list (in order). git's sequence editor is replaced by a copy of
    /// our list; <c>reword</c> becomes a <c>pick</c> followed by an <c>exec</c> that amends the message from a file, and
    /// squashes keep the combined message, so git never opens an editor.
    /// </summary>
    public async Task RebaseInteractiveAsync(string onto, IReadOnlyList<RebaseStep> steps, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "codalus-rebase-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var todo = new StringBuilder();
            var index = 0;
            foreach (var step in steps)
            {
                switch (step.Action)
                {
                    case "pick" or "squash" or "fixup" or "drop":
                        todo.Append(step.Action).Append(' ').Append(step.Hash).Append('\n');
                        break;
                    case "reword":
                        var message = Path.Combine(folder, $"message{(index++).ToString(CultureInfo.InvariantCulture)}.txt");
                        await File.WriteAllTextAsync(message, step.Message ?? step.Subject, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
                        todo.Append("pick ").Append(step.Hash).Append('\n');
                        todo.Append("exec git commit --amend --only --quiet --cleanup=strip --file=").Append(ShellQuote(message)).Append('\n');
                        break;
                    default:
                        throw new ArgumentException($"Unknown rebase action '{step.Action}'.", nameof(steps));
                }
            }

            var todoFile = Path.Combine(folder, "git-rebase-todo");
            await File.WriteAllTextAsync(todoFile, todo.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            // git runs the sequence editor through its shell (sh, also on Windows) with the todo file's path.
            await MutateWithoutEditorAsync(["rebase", "--interactive", onto], cancellationToken, new Dictionary<string, string>
            {
                ["GIT_SEQUENCE_EDITOR"] = "cp " + ShellQuote(todoFile),
            }).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>A path for git's shell (sh): forward slashes, single quotes.</summary>
    private static string ShellQuote(string path) => "'" + path.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    // ------------------------------------------------------------------ tags

    public async Task<IReadOnlyList<GitTag>> TagsAsync(CancellationToken cancellationToken = default)
    {
        var (_, output, _) = await GitAsync(["for-each-ref", "--sort=-creatordate", "--format=%(refname:short)%1f%(objectname)%1f%(*objectname)%1f%(contents:subject)", "refs/tags"], cancellationToken, readOnly: true).ConfigureAwait(false);
        return [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\x1f'))
            .Where(parts => parts.Length == 4)
            .Select(parts => new GitTag(parts[0], parts[2].Length > 0 ? parts[2] : parts[1], parts[2].Length > 0 && parts[3].Length > 0 ? parts[3] : null))];
    }

    /// <summary>Creates a tag at a commit (HEAD when null); with a message it's an annotated tag.</summary>
    public Task CreateTagAsync(string name, string? hash, string? message, CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "tag" };
        if (message is { Length: > 0 })
        {
            arguments.Add("--annotate");
            arguments.Add("--message=" + message);
        }

        arguments.Add(name);
        if (hash is not null)
        {
            arguments.Add(hash);
        }

        return MutateAsync(arguments, cancellationToken);
    }

    public Task DeleteTagAsync(string name, CancellationToken cancellationToken = default) => MutateAsync(["tag", "--delete", name], cancellationToken);

    public Task PushTagAsync(string name, string remote = "origin", CancellationToken cancellationToken = default) =>
        MutateAsync(["push", remote, "refs/tags/" + name], cancellationToken);

    public Task DeleteRemoteTagAsync(string name, string remote = "origin", CancellationToken cancellationToken = default) =>
        MutateAsync(["push", remote, "--delete", "refs/tags/" + name], cancellationToken);

    // ------------------------------------------------------------------ remotes

    public async Task<IReadOnlyList<GitRemote>> RemotesAsync(CancellationToken cancellationToken = default)
    {
        var (_, output, _) = await GitAsync(["remote", "--verbose"], cancellationToken, readOnly: true).ConfigureAwait(false);
        var remotes = new Dictionary<string, (string? Fetch, string? Push)>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            var entry = remotes.GetValueOrDefault(parts[0]);
            remotes[parts[0]] = parts[2] == "(push)" ? (entry.Fetch, parts[1]) : (parts[1], entry.Push);
        }

        return [.. remotes.Select(r => new GitRemote(r.Key, r.Value.Fetch ?? r.Value.Push ?? string.Empty, r.Value.Push != r.Value.Fetch ? r.Value.Push : null))];
    }

    public Task AddRemoteAsync(string name, string url, CancellationToken cancellationToken = default) => MutateAsync(["remote", "add", name, url], cancellationToken);

    public Task RemoveRemoteAsync(string name, CancellationToken cancellationToken = default) => MutateAsync(["remote", "remove", name], cancellationToken);

    public Task RenameRemoteAsync(string name, string newName, CancellationToken cancellationToken = default) => MutateAsync(["remote", "rename", name, newName], cancellationToken);

    public Task SetRemoteUrlAsync(string name, string url, CancellationToken cancellationToken = default) => MutateAsync(["remote", "set-url", name, url], cancellationToken);

    // ------------------------------------------------------------------ per-line staging

    /// <summary>Stages the chosen lines (indexes into the hunk's lines) of a work-tree hunk.</summary>
    public Task StageLinesAsync(FileDiff diff, DiffHunk hunk, IReadOnlySet<int> lines, CancellationToken cancellationToken = default) =>
        MutateAsync(["apply", "--cached", "--unidiff-zero", "--whitespace=nowarn", "-"], cancellationToken, diff.PatchForLines(hunk, lines));

    /// <summary>Unstages the chosen lines of a staged hunk.</summary>
    public Task UnstageLinesAsync(FileDiff diff, DiffHunk hunk, IReadOnlySet<int> lines, CancellationToken cancellationToken = default) =>
        MutateAsync(["apply", "--cached", "-R", "--unidiff-zero", "--whitespace=nowarn", "-"], cancellationToken, diff.PatchForLines(hunk, lines, reverse: true));

    /// <summary>Reverts the chosen lines of a work-tree hunk in the file.</summary>
    public Task DiscardLinesAsync(FileDiff diff, DiffHunk hunk, IReadOnlySet<int> lines, CancellationToken cancellationToken = default) =>
        MutateAsync(["apply", "-R", "--unidiff-zero", "--whitespace=nowarn", "-"], cancellationToken, diff.PatchForLines(hunk, lines, reverse: true));

    // ------------------------------------------------------------------ repository files

    /// <summary>Adds a file or folder to the repository's root <c>.gitignore</c> (Add to .gitignore).</summary>
    public async Task IgnoreAsync(string path, CancellationToken cancellationToken = default)
    {
        var relative = Relative(path);
        var pattern = "/" + relative + (Directory.Exists(path) ? "/" : string.Empty);
        var file = Path.Combine(Root!, ".gitignore");
        var existing = File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false) : string.Empty;
        if (existing.Split('\n').Any(l => l.TrimEnd('\r') == pattern))
        {
            return;
        }

        var separator = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : string.Empty;
        await File.AppendAllTextAsync(file, separator + pattern + "\n", new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The commit message template (<c>commit.template</c>), or null when none is configured.</summary>
    public async Task<string?> CommitTemplateAsync(CancellationToken cancellationToken = default)
    {
        var (code, output, _) = await RunAsync(Root!, ["config", "--path", "--get", "commit.template"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        if (code != 0 || output.Trim() is not { Length: > 0 } file)
        {
            return null;
        }

        var full = Path.IsPathRooted(file) ? file : Path.Combine(Root!, file);
        return File.Exists(full) ? await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false) : null;
    }

    // ------------------------------------------------------------------ submodules

    public async Task<IReadOnlyList<GitSubmodule>> SubmodulesAsync(CancellationToken cancellationToken = default)
    {
        var (_, output, _) = await GitAsync(["submodule", "status"], cancellationToken, readOnly: true).ConfigureAwait(false);
        var submodules = new List<GitSubmodule>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var state = line[0];
            var parts = line[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                submodules.Add(new GitSubmodule(Path.Combine(Root!, parts[1].Replace('/', Path.DirectorySeparatorChar)), parts[0], state));
            }
        }

        return submodules;
    }

    /// <summary>Initializes and updates every submodule (recursively).</summary>
    public Task UpdateSubmodulesAsync(CancellationToken cancellationToken = default) =>
        MutateAsync(["submodule", "update", "--init", "--recursive"], cancellationToken);

    // ------------------------------------------------------------------ creating repositories

    /// <summary>Creates a repository in <paramref name="folder"/> (branch <c>main</c>) with a .NET <c>.gitignore</c> when it has none.</summary>
    public async Task InitAsync(string folder, CancellationToken cancellationToken = default)
    {
        await RunAsync(folder, ["init", "--quiet", "--initial-branch=main"], cancellationToken).ConfigureAwait(false);
        var ignore = Path.Combine(folder, ".gitignore");
        if (!File.Exists(ignore))
        {
            await File.WriteAllTextAsync(ignore, DotnetGitIgnore, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Clones <paramref name="url"/> into <paramref name="destination"/> (which must not exist or be empty).</summary>
    public async Task CloneAsync(string url, string destination, CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        await RunAsync(parent, ["clone", "--recurse-submodules", url, destination], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A .NET <c>.gitignore</c>: build outputs, IDE state and test results.</summary>
    public const string DotnetGitIgnore = """
        # Build results
        [Bb]in/
        [Oo]bj/
        [Ll]og/
        [Ll]ogs/
        artifacts/
        *.nupkg
        *.snupkg

        # IDE state
        .vs/
        .idea/
        .codalus/state/
        *.user
        *.suo

        # Test results and coverage
        [Tt]est[Rr]esult*/
        *.trx
        coverage*.json
        coverage*.xml
        *.coverage

        # NuGet
        packages/
        project.lock.json
        project.fragment.lock.json

        # OS files
        .DS_Store
        Thumbs.db

        """;
}
