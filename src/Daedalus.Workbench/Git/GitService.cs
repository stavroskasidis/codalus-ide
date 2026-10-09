using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Daedalus.Workbench.Git;

/// <summary>
/// Source control through the git CLI (ADR 0008): one process per operation, porcelain output only. Tracks one
/// repository (the open solution's). Events may be raised on any thread.
/// </summary>
public sealed class GitService : IDisposable
{
    public const string EnvironmentVariable = "DAEDALUS_GIT";

    private readonly string _git;
    private readonly bool _watch;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private CancellationTokenSource _lifetime = new();

    public GitService(string? gitPath = null, bool watch = true)
    {
        _git = gitPath ?? (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv ? fromEnv : "git");
        _watch = watch;
    }

    /// <summary>The repository's root folder, or null when none is open.</summary>
    public string? Root { get; private set; }

    public GitStatus? Status { get; private set; }

    /// <summary>The status changed (after a refresh).</summary>
    public event EventHandler? StatusChanged;

    /// <summary>A file's index or HEAD version may have changed (gutter markers re-read it).</summary>
    public event EventHandler? IndexChanged;

    /// <summary>Every git command and its outcome, for the Git output channel.</summary>
    public event EventHandler<string>? Log;

    // ------------------------------------------------------------------ running git

    /// <summary>Runs git in <paramref name="directory"/>; throws <see cref="GitException"/> on a non-zero exit (unless <paramref name="check"/> is false).</summary>
    public async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? input = null, bool check = true, bool readOnly = false)
    {
        var start = new ProcessStartInfo(_git)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var argument in (string[])["-c", "core.quotepath=false", "-c", "color.ui=false", .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["LC_ALL"] = "C";
        if (readOnly)
        {
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new GitException("git did not start.", -1);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitException($"git was not found ({ex.Message}). Install git, or set {EnvironmentVariable}.", -1);
        }

        using (process)
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                throw;
            }

            var result = (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
            if (!readOnly)
            {
                Log?.Invoke(this, $"> git {string.Join(' ', arguments)}" + (result.ExitCode == 0 ? string.Empty : $"\n{result.Item3.TrimEnd()}"));
            }

            if (check && result.ExitCode != 0)
            {
                var message = (result.Item3.Trim().Length > 0 ? result.Item3 : result.Item2).Trim();
                throw new GitException(message.Length > 0 ? message : $"git {(arguments.Count > 0 ? arguments[0] : string.Empty)} failed (exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}).", result.ExitCode);
            }

            return result;
        }
    }

    private Task<(int ExitCode, string Output, string Error)> GitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? input = null, bool readOnly = false) =>
        RunAsync(Root ?? throw new InvalidOperationException("No git repository is open."), arguments, cancellationToken, input, check: true, readOnly);

    // ------------------------------------------------------------------ repository

    /// <summary>Opens the repository that contains <paramref name="path"/> (a folder or file); false when there is none.</summary>
    public async Task<bool> OpenAsync(string path, CancellationToken cancellationToken)
    {
        Close();
        var directory = Directory.Exists(path) ? path : Path.GetDirectoryName(path)!;
        var (code, output, _) = await RunAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        if (code != 0 || output.Trim().Length == 0)
        {
            return false;
        }

        Root = Path.GetFullPath(output.Trim());
        _lifetime = new CancellationTokenSource();
        if (_watch)
        {
            StartWatching();
        }

        await RefreshAsync(_lifetime.Token).ConfigureAwait(false);
        return true;
    }

    public void Close()
    {
        _lifetime.Cancel();
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _debounce = null;
        var had = Root is not null;
        Root = null;
        Status = null;
        if (had)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Reads the status again.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Root is not { } root)
        {
            return;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (_, output, _) = await RunAsync(root, ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all"], cancellationToken, readOnly: true).ConfigureAwait(false);
            var (gitDirCode, gitDir, _) = await RunAsync(root, ["rev-parse", "--absolute-git-dir"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
            var merging = gitDirCode == 0 && File.Exists(Path.Combine(gitDir.Trim(), "MERGE_HEAD"));
            if (Root != root)
            {
                return;
            }

            Status = GitParsers.ParseStatus(root, output, merging);
        }
        catch (GitException ex)
        {
            Log?.Invoke(this, "status failed: " + ex.Message);
            return;
        }
        finally
        {
            _refreshGate.Release();
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
        IndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartWatching()
    {
        _debounce = new Timer(_ => _ = RefreshSafeAsync(), null, Timeout.Infinite, Timeout.Infinite);
        try
        {
            _watcher = new FileSystemWatcher(Root!)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            Log?.Invoke(this, "Not watching the repository for changes: " + ex.Message);
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Inside .git only the index, HEAD and refs matter; build outputs churn but are usually ignored, so status is cheap.
        var relative = Path.GetRelativePath(Root ?? string.Empty, e.FullPath).Replace('\\', '/');
        if (relative.StartsWith(".git/", StringComparison.Ordinal)
            && !(relative is ".git/index" or ".git/HEAD" or ".git/MERGE_HEAD" || relative.StartsWith(".git/refs/", StringComparison.Ordinal)))
        {
            return;
        }

        if (relative.EndsWith(".lock", StringComparison.Ordinal))
        {
            return;
        }

        _debounce?.Change(400, Timeout.Infinite);
    }

    private async Task RefreshSafeAsync()
    {
        try
        {
            await RefreshAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task MutateAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? input = null)
    {
        try
        {
            await GitAsync(arguments, cancellationToken, input).ConfigureAwait(false);
        }
        finally
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private string Relative(string path) => Path.GetRelativePath(Root!, path).Replace('\\', '/');

    // ------------------------------------------------------------------ staging

    public Task StageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        MutateAsync(["add", "--all", "--", .. paths.Select(Relative)], cancellationToken);

    /// <summary>Unstages files (before the first commit, removes them from the index).</summary>
    public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        Status?.Head is null
            ? MutateAsync(["rm", "--cached", "-r", "--quiet", "--", .. paths.Select(Relative)], cancellationToken)
            : MutateAsync(["restore", "--staged", "--", .. paths.Select(Relative)], cancellationToken);

    /// <summary>Discards work-tree changes: tracked files go back to the index version; untracked files are deleted.</summary>
    public async Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var files = Status?.Files ?? [];
        var untracked = paths.Where(p => files.Any(f => f.IsUntracked && Workbench.Projects.SolutionService.PathComparer.Equals(f.Path, p))).ToList();
        var tracked = paths.Except(untracked).ToList();
        if (tracked.Count > 0)
        {
            await GitAsync(["restore", "--worktree", "--", .. tracked.Select(Relative)], cancellationToken).ConfigureAwait(false);
        }

        if (untracked.Count > 0)
        {
            await GitAsync(["clean", "-f", "-q", "--", .. untracked.Select(Relative)], cancellationToken).ConfigureAwait(false);
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The diff of a file: work tree against the index, or (<paramref name="staged"/>) the index against HEAD.</summary>
    public async Task<FileDiff?> DiffAsync(string path, bool staged, CancellationToken cancellationToken = default)
    {
        var status = Status?.Files.FirstOrDefault(f => Workbench.Projects.SolutionService.PathComparer.Equals(f.Path, path));
        if (status is { IsUntracked: true })
        {
            // Untracked: everything is new.
            var (_, untracked, _) = await RunAsync(Root!, ["diff", "--no-index", "--no-ext-diff", "--", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", Relative(path)], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
            return GitParsers.ParseDiff(Root!, untracked) is [var first, ..] ? first : null;
        }

        var (_, output, _) = await GitAsync(staged ? ["diff", "--cached", "--no-ext-diff", "-M", "--", Relative(path)] : ["diff", "--no-ext-diff", "--", Relative(path)], cancellationToken, readOnly: true).ConfigureAwait(false);
        return GitParsers.ParseDiff(Root!, output) is [var diff, ..] ? diff : null;
    }

    /// <summary>Stages one hunk of a work-tree diff.</summary>
    public Task StageHunkAsync(FileDiff diff, DiffHunk hunk, CancellationToken cancellationToken = default) =>
        MutateAsync(["apply", "--cached", "--whitespace=nowarn", "-"], cancellationToken, diff.PatchFor(hunk));

    /// <summary>Unstages one hunk of a staged diff.</summary>
    public Task UnstageHunkAsync(FileDiff diff, DiffHunk hunk, CancellationToken cancellationToken = default) =>
        MutateAsync(["apply", "--cached", "-R", "--whitespace=nowarn", "-"], cancellationToken, diff.PatchFor(hunk));

    /// <summary>Reverts one hunk of a work-tree diff in the file.</summary>
    public Task DiscardHunkAsync(FileDiff diff, DiffHunk hunk, CancellationToken cancellationToken = default) =>
        MutateAsync(["apply", "-R", "--whitespace=nowarn", "-"], cancellationToken, diff.PatchFor(hunk));

    /// <summary>A file's content at <paramref name="revision"/> (<c>:</c> = the index, <c>HEAD</c>…); null when it isn't there.</summary>
    public async Task<string?> ShowAsync(string path, string revision, CancellationToken cancellationToken = default)
    {
        if (Root is not { } root)
        {
            return null;
        }

        var spec = revision == ":" ? ":" + Relative(path) : $"{revision}:{Relative(path)}";
        var (code, output, _) = await RunAsync(root, ["show", spec], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
        return code == 0 ? output : null;
    }

    // ------------------------------------------------------------------ commits

    public async Task<string?> LastCommitMessageAsync(CancellationToken cancellationToken = default)
    {
        if (Status?.Head is null)
        {
            return null;
        }

        var (_, output, _) = await GitAsync(["log", "-1", "--format=%B"], cancellationToken, readOnly: true).ConfigureAwait(false);
        return output.TrimEnd();
    }

    /// <summary>Commits the staged changes (<paramref name="all"/>: every tracked change), or amends the last commit.</summary>
    public Task CommitAsync(string message, bool amend, bool all, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            throw new GitException("Enter a commit message.", -1);
        }

        var arguments = new List<string> { "commit", "--file=-", "--cleanup=strip" };
        if (amend)
        {
            arguments.Add("--amend");
        }

        if (all)
        {
            arguments.Add("--all");
        }

        return MutateAsync(arguments, cancellationToken, message);
    }

    // ------------------------------------------------------------------ branches

    public async Task<IReadOnlyList<GitBranch>> BranchesAsync(CancellationToken cancellationToken = default)
    {
        var (_, output, _) = await GitAsync(
            ["for-each-ref", "--format=%(refname)%00%(HEAD)%00%(upstream:short)%00%(objectname:short)", "refs/heads", "refs/remotes"],
            cancellationToken, readOnly: true).ConfigureAwait(false);
        var branches = new List<GitBranch>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\0');
            if (parts.Length < 4 || parts[0].EndsWith("/HEAD", StringComparison.Ordinal))
            {
                continue;
            }

            var remote = parts[0].StartsWith("refs/remotes/", StringComparison.Ordinal);
            var name = parts[0][(remote ? "refs/remotes/".Length : "refs/heads/".Length)..];
            branches.Add(new GitBranch(name, remote, parts[1] == "*", parts[2].Length > 0 ? parts[2] : null, parts[3]));
        }

        return branches;
    }

    /// <summary>Switches to a branch; a remote branch (<c>origin/x</c>) gets a local tracking branch <c>x</c>.</summary>
    public Task SwitchAsync(GitBranch branch, CancellationToken cancellationToken = default) =>
        branch.IsRemote
            ? MutateAsync(["switch", "--track", branch.Name], cancellationToken)
            : MutateAsync(["switch", branch.Name], cancellationToken);

    public Task CreateBranchAsync(string name, bool switchTo, CancellationToken cancellationToken = default) =>
        switchTo ? MutateAsync(["switch", "-c", name], cancellationToken) : MutateAsync(["branch", name], cancellationToken);

    /// <summary>Merges a branch into the current one. Conflicts leave the merge in progress (see <see cref="GitStatus.IsMerging"/>).</summary>
    public async Task MergeAsync(string branch, CancellationToken cancellationToken = default)
    {
        try
        {
            await GitAsync(["merge", "--no-edit", branch], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task AbortMergeAsync(CancellationToken cancellationToken = default) => MutateAsync(["merge", "--abort"], cancellationToken);

    // ------------------------------------------------------------------ remotes

    public Task FetchAsync(CancellationToken cancellationToken = default) => MutateAsync(["fetch", "--prune"], cancellationToken);

    public Task PullAsync(CancellationToken cancellationToken = default) => MutateAsync(["pull"], cancellationToken);

    /// <summary>Pushes the current branch; the first push sets the upstream on the first remote (usually origin).</summary>
    public async Task PushAsync(CancellationToken cancellationToken = default)
    {
        if (Status is { Branch: { } branch, Upstream: null })
        {
            var (_, remotes, _) = await GitAsync(["remote"], cancellationToken, readOnly: true).ConfigureAwait(false);
            var remote = remotes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is { Length: > 0 } names
                ? names.Contains("origin") ? "origin" : names[0]
                : throw new GitException("This repository has no remote to push to.", -1);
            await MutateAsync(["push", "-u", remote, branch], cancellationToken).ConfigureAwait(false);
            return;
        }

        await MutateAsync(["push"], cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ blame

    public async Task<IReadOnlyList<BlameRange>> BlameAsync(string path, string? contents, CancellationToken cancellationToken = default)
    {
        // --contents - blames the editor's text, so annotations line up with unsaved edits.
        var arguments = contents is null
            ? new List<string> { "blame", "--porcelain", "--", Relative(path) }
            : ["blame", "--porcelain", "--contents", "-", "--", Relative(path)];
        var (_, output, _) = await GitAsync(arguments, cancellationToken, contents, readOnly: true).ConfigureAwait(false);
        return GitParsers.ParseBlame(output);
    }

    public void Dispose()
    {
        Close();
        _lifetime.Dispose();
        _refreshGate.Dispose();
    }
}
