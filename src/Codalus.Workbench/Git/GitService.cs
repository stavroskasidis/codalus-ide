using System.Diagnostics;
using System.Globalization;
using System.Text;
using Codalus.Platform;

namespace Codalus.Workbench.Git;

/// <summary>
/// Source control through the git CLI (ADR 0008): one process per operation, porcelain output only. Tracks one
/// repository (the open solution's), and the solution's other repositories as siblings while all are shown (ADR 0085).
/// Events may be raised on any thread.
/// </summary>
public sealed partial class GitService : IDisposable
{
    public const string EnvironmentVariable = "CODALUS_GIT";

    private readonly string _git;
    private readonly bool _watch;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private CancellationTokenSource _lifetime = new();

    // What the watcher saw since the last status read (1 when seen): a ref, HEAD or the stashes moved (the Git Repository
    // document and the stash list read them again on StatusChanged), the index was written (gutter markers read it), or a
    // file the status lists as changed was written (its diff changed, not its status).
    private int _sawRefs;
    private int _sawIndex;
    private int _sawChangedFile;
    private volatile HashSet<string> _changedPaths = [];
    private long _lastRead;

    // The writes the watcher saw, and how many of them its last read covered; the reads running; when the next is due.
    private int _watcherEvents;
    private int _watcherEventsRead;
    private int _readsRunning;
    private long _readDueAt;

    public GitService(string? gitPath = null, bool watch = true)
    {
        _git = gitPath ?? (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv ? fromEnv : "git");
        _watch = watch;
    }

    /// <summary>The repository's root folder, or null when none is open.</summary>
    public string? Root { get; private set; }

    public GitStatus? Status { get; private set; }

    /// <summary>
    /// The repository may have changed: after every <see cref="RefreshAsync"/> and every command that writes (a commit, a
    /// tag, a stash dropped), and after a read the watcher asked for when the status differs, a ref or the stashes moved,
    /// or a changed file was written. A read the watcher asked for that found everything as it was raises nothing: a
    /// build's outputs (bin/) churn, and each read rebuilt Git Changes, the open diffs and the Git Repository document.
    /// </summary>
    public event EventHandler? StatusChanged;

    /// <summary>A file's index or HEAD version may have changed (gutter markers re-read it).</summary>
    public event EventHandler? IndexChanged;

    /// <summary>
    /// Whether a build is running, asked at each write the watcher sees (set by the shell; the siblings ask this one, All
    /// Repositories, ADR 0085): its outputs churn, so while one runs the watcher reads the status at most every 2 s. Asked
    /// rather than told, so a build that ends without saying so leaves no throttle behind.
    /// </summary>
    public Func<bool>? IsBuilding { get; set; }

    private bool BuildRunning() => IsBuilding?.Invoke() == true;

    /// <summary>
    /// A build ended (from any thread): the status read the throttle put off comes after the watcher's usual wait, here and
    /// in the siblings. None when the watcher saw nothing since its last read.
    /// </summary>
    public void OnBuildEnded()
    {
        foreach (var sibling in Siblings)
        {
            sibling.OnBuildEnded();
        }

        if (Volatile.Read(ref _watcherEvents) != Volatile.Read(ref _watcherEventsRead))
        {
            ArmRead(WatcherDelay);
        }
    }

    /// <summary>How long the watcher waits for a quiet moment before it reads the status.</summary>
    private const int WatcherDelay = 400;

    /// <summary>While a build runs, the least time between two reads the watcher asks for.</summary>
    private const int BuildReadInterval = 2000;

    /// <summary>Every git command and its outcome, for the Git output channel.</summary>
    public event EventHandler<string>? Log;

    // ------------------------------------------------------------------ running git

    /// <summary>
    /// Runs git in <paramref name="directory"/>; throws <see cref="GitException"/> on a non-zero exit (unless <paramref name="check"/> is false).
    /// The process starts on the thread pool, never on the caller's thread, which is often the UI thread (Sync, Git
    /// Changes, the status bar): starting a process can take long. On Windows, CreateProcess runs under a lock that every
    /// Process.Start in the IDE shares, and the antivirus may scan it; on Linux, it forks a large process (the Windows
    /// command sweep saw Sync, which started pull and then push on the UI thread, hold it for 2 s).
    /// </summary>
    public async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? input = null, bool check = true, bool readOnly = false,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await RunOnceAsync(directory, arguments, input, readOnly, environment, cancellationToken).ConfigureAwait(false);
            if (!readOnly && attempt < s_lockRetryDelays.Length && IsLockBusy(result.ExitCode, result.Error))
            {
                await Task.Delay(s_lockRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (check && result.ExitCode != 0)
            {
                var message = (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim();
                throw new GitException(message.Length > 0 ? message : $"git {(arguments.Count > 0 ? arguments[0] : string.Empty)} failed (exit code {result.ExitCode.ToString(CultureInfo.InvariantCulture)}).", result.ExitCode);
            }

            return result;
        }
    }

    /// <summary>
    /// Whether git failed only because a lock file it creates (<c>index.lock</c>, a ref's) exists: another git client
    /// (a terminal, another editor) may be writing for a moment, and on Windows the antivirus or the search indexer can
    /// hold the last command's lock open after git deleted it. Codalus' own reads take no lock (<see cref="Start"/>).
    /// Such a command changed nothing, so a write tries again a few times before it reports the lock.
    /// </summary>
    internal static bool IsLockBusy(int exitCode, string error) => exitCode != 0 && error.Contains(".lock': File exists", StringComparison.Ordinal);

    /// <summary>The waits before a write that found a lock busy tries again: about 1.5 s in all.</summary>
    private static readonly TimeSpan[] s_lockRetryDelays = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800)];

    private async Task<(int ExitCode, string Output, string Error)> RunOnceAsync(
        string directory, IReadOnlyList<string> arguments, string? input, bool readOnly, IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
    {
        await Codalus.Rpc.ProcessEnvironment.WhenReadyAsync(cancellationToken).ConfigureAwait(false); // the login shell's PATH and proxies (M56)
        var process = await Task.Run(() => Start(directory, arguments, input is not null, readOnly, environment), CancellationToken.None).ConfigureAwait(false);
        using (process)
        {
            if (input is not null)
            {
                await WriteInputAsync(process, input, cancellationToken).ConfigureAwait(false);
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
                    ProcessTree.Kill(process);
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

            return result;
        }
    }

    /// <summary>
    /// Gives git its input (pathspecs, a commit message, a patch). git may end before it reads it all, when it can't take
    /// a lock: the pipe is then broken, and its exit code and message, not the write, say what happened (a lock that's
    /// busy is tried again).
    /// </summary>
    private static async Task WriteInputAsync(Process process, string input, CancellationToken cancellationToken)
    {
        try
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Runs git with bytes in and out (a file's content, a patch), in the repository: no text decoding, so a byte
    /// order mark, line endings and any encoding pass unchanged (#86).
    /// </summary>
    private async Task<(int ExitCode, byte[] Output, string Error)> RunBytesAsync(IReadOnlyList<string> arguments, byte[]? input, bool readOnly, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await RunBytesOnceAsync(arguments, input, readOnly, cancellationToken).ConfigureAwait(false);
            if (readOnly || attempt >= s_lockRetryDelays.Length || !IsLockBusy(result.ExitCode, result.Error))
            {
                return result;
            }

            await Task.Delay(s_lockRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(int ExitCode, byte[] Output, string Error)> RunBytesOnceAsync(IReadOnlyList<string> arguments, byte[]? input, bool readOnly, CancellationToken cancellationToken)
    {
        var root = Root ?? throw new InvalidOperationException("No git repository is open.");
        await Codalus.Rpc.ProcessEnvironment.WhenReadyAsync(cancellationToken).ConfigureAwait(false); // the login shell's PATH and proxies (M56)
        var process = await Task.Run(() => Start(root, arguments, input is not null, readOnly, null), CancellationToken.None).ConfigureAwait(false);
        using (process)
        {
            var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            if (input is not null)
            {
                try
                {
                    await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
                    process.StandardInput.Close();
                }
                catch (IOException)
                {
                    // git ended before it read its input: its exit code and message say why (see WriteInputAsync).
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    ProcessTree.Kill(process);
                }
                catch (InvalidOperationException)
                {
                }

                throw;
            }

            await copy.ConfigureAwait(false);
            var result = (process.ExitCode, output.ToArray(), await error.ConfigureAwait(false));
            if (!readOnly)
            {
                Log?.Invoke(this, $"> git {string.Join(' ', arguments)}" + (result.ExitCode == 0 ? string.Empty : $"\n{result.Item3.TrimEnd()}"));
            }

            return result;
        }
    }

    private Process Start(string directory, IReadOnlyList<string> arguments, bool redirectInput, bool readOnly, IReadOnlyDictionary<string, string>? environment)
    {
        var start = new ProcessStartInfo(_git)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = redirectInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),

            // Paths, messages and patches reach git as UTF-8: Windows' default is the console's code page.
            StandardInputEncoding = redirectInput ? new UTF8Encoding(false) : null,
        };
        // A read takes no lock. GIT_OPTIONAL_LOCKS keeps status from writing the index, but not a working-tree diff: git
        // diff refreshes the index's stat data under index.lock whenever a file's timestamps changed but not its content
        // (diff.autoRefreshIndex), so a diff document, which reloads on every status change, could hold the lock while
        // Stage Active File ran git add (the Windows command sweep's "Unable to create '.../index.lock': File exists", run
        // 39937763661). Without the refresh, the diff reads such a file's content and still shows no change.
        string[] options = readOnly
            ? ["-c", "core.quotepath=false", "-c", "color.ui=false", "-c", "diff.autoRefreshIndex=false"]
            : ["-c", "core.quotepath=false", "-c", "color.ui=false"];
        foreach (var argument in (string[])[.. options, .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["LC_ALL"] = "C";
        if (readOnly)
        {
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        }

        // The shell's askpass (M13b): git and ssh ask it for what no credential helper answered.
        foreach (var (name, value) in AskpassEnvironment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        try
        {
            return Process.Start(start) ?? throw new GitException("git did not start.", -1);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitException($"git was not found ({ex.Message}). Install git, or set {EnvironmentVariable}.", -1);
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
        CloseSiblings();
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

    /// <summary>Reads the status again, and raises <see cref="StatusChanged"/> and <see cref="IndexChanged"/>.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default) => ReadStatusAsync(fromWatcher: false, cancellationToken);

    /// <summary>
    /// Reads the status. A read the watcher asked for raises <see cref="StatusChanged"/> only when something may have
    /// changed (the status, a ref, the stashes, a changed file's content) and <see cref="IndexChanged"/> only when the
    /// status or the index did.
    /// </summary>
    private async Task ReadStatusAsync(bool fromWatcher, CancellationToken cancellationToken)
    {
        if (Root is not { } root)
        {
            return;
        }

        Interlocked.Increment(ref _readsRunning);
        try
        {
            await ReadStatusOnceAsync(root, fromWatcher, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _readsRunning);
        }
    }

    private async Task ReadStatusOnceAsync(string root, bool fromWatcher, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool statusChanged, indexChanged, refs = false, index = false, changedFile = false, read = false;
        try
        {
            // Taken before git runs: what the watcher sees meanwhile is for the next read.
            Volatile.Write(ref _lastRead, Stopwatch.GetTimestamp());
            if (fromWatcher)
            {
                Volatile.Write(ref _watcherEventsRead, Volatile.Read(ref _watcherEvents));
            }

            refs = Interlocked.Exchange(ref _sawRefs, 0) == 1;
            index = Interlocked.Exchange(ref _sawIndex, 0) == 1;
            changedFile = Interlocked.Exchange(ref _sawChangedFile, 0) == 1;
            var (_, output, _) = await RunAsync(root, ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all"], cancellationToken, readOnly: true).ConfigureAwait(false);
            var (gitDirCode, gitDir, _) = await RunAsync(root, ["rev-parse", "--absolute-git-dir"], cancellationToken, check: false, readOnly: true).ConfigureAwait(false);
            var merging = gitDirCode == 0 && File.Exists(Path.Combine(gitDir.Trim(), "MERGE_HEAD"));
            if (Root != root)
            {
                return;
            }

            var status = GitParsers.ParseStatus(root, output, merging) with { Operation = gitDirCode == 0 ? OperationIn(gitDir.Trim()) : null };
            var differs = !SameStatus(Status, status);
            if (differs)
            {
                _changedPaths = status.Files.Select(f => f.RelativePath).ToHashSet(Workbench.Projects.SolutionService.PathComparer);
            }

            Status = status;
            GitDirectory = gitDirCode == 0 ? gitDir.Trim() : null;
            statusChanged = !fromWatcher || differs || refs || index || changedFile;
            indexChanged = !fromWatcher || differs || index;
            read = true;
        }
        catch (GitException ex)
        {
            Log?.Invoke(this, "status failed: " + ex.Message);
            return;
        }
        finally
        {
            // A read that failed or was canceled (a command's refresh, stopped by its token) leaves what the watcher saw to
            // the next one: a tag, a stash dropped or the index written changes no status, and would raise nothing then.
            if (!read)
            {
                Interlocked.Or(ref _sawRefs, refs ? 1 : 0);
                Interlocked.Or(ref _sawIndex, index ? 1 : 0);
                Interlocked.Or(ref _sawChangedFile, changedFile ? 1 : 0);
            }

            _refreshGate.Release();
        }

        if (statusChanged)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        if (indexChanged)
        {
            IndexChanged?.Invoke(this, EventArgs.Empty);
        }

        Interlocked.Increment(ref _statusReads);
    }

    /// <summary>Whether two statuses say the same: the branch, its commits, the operation and every file, letter for letter.</summary>
    internal static bool SameStatus(GitStatus? before, GitStatus after) =>
        before is not null && before with { Files = after.Files } == after && before.Files.SequenceEqual(after.Files);

    private void StartWatching()
    {
        _debounce = new Timer(_ => _ = WatcherReadAsync(), null, Timeout.Infinite, Timeout.Infinite);
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
        var relative = Path.GetRelativePath(Root ?? string.Empty, e.FullPath).Replace('\\', '/');
        if (!ChangesStatus(relative))
        {
            return;
        }

        if (relative == ".git/index")
        {
            Volatile.Write(ref _sawIndex, 1);
        }
        else if (relative.StartsWith(".git/", StringComparison.Ordinal))
        {
            Volatile.Write(ref _sawRefs, 1);
        }
        else if (_changedPaths.Contains(relative))
        {
            Volatile.Write(ref _sawChangedFile, 1);
        }

        Interlocked.Increment(ref _watcherEvents);

        // While a build runs, a read at most every 2 s; otherwise once writes pause.
        var delay = WatcherDelay;
        if (BuildRunning())
        {
            var since = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastRead)).TotalMilliseconds;
            delay = (int)Math.Max(WatcherDelay, BuildReadInterval - since);
        }

        ArmRead(delay);
    }

    /// <summary>The watcher's status read, in <paramref name="delay"/> ms (asking again moves it); none once closed.</summary>
    private void ArmRead(int delay)
    {
        Volatile.Write(ref _readDueAt, Stopwatch.GetTimestamp() + (delay * Stopwatch.Frequency / 1000));
        try
        {
            _debounce?.Change(delay, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Closed meanwhile: Close and a sibling's disposal run on other threads than the watcher and a build's end.
        }
    }

    /// <summary>Whether a status read is running, or due for writes the watcher saw (tests wait for it to settle).</summary>
    internal bool IsReadPending => Volatile.Read(ref _readsRunning) > 0 || Volatile.Read(ref _watcherEvents) != Volatile.Read(ref _watcherEventsRead);

    /// <summary>When the watcher's next status read is due, as a <see cref="Stopwatch"/> timestamp (tests).</summary>
    internal long ReadDue => Volatile.Read(ref _readDueAt);

    /// <summary>Whether a change to a path (relative to the worktree, with <c>/</c>) can change the repository's status.</summary>
    internal static bool ChangesStatus(string relative)
    {
        // Inside .git only the index, HEAD, refs and the stashes' log matter (a stash dropped below the newest changes only
        // the log; a branch or tag deleted, only packed-refs); build outputs churn but are usually ignored, so status is cheap.
        if (relative.StartsWith(".git/", StringComparison.Ordinal)
            && !(relative is ".git/index" or ".git/HEAD" or ".git/MERGE_HEAD" or ".git/packed-refs" or ".git/logs/refs/stash"
                 || relative.StartsWith(".git/refs/", StringComparison.Ordinal) || relative.StartsWith(".git/reftable/", StringComparison.Ordinal)))
        {
            return false;
        }

        // obj/ is .NET's intermediate output, which every .NET .gitignore leaves out: design-time builds, restores and
        // the C# service's state, which it writes after each background pass (every pause in typing). Each was a full
        // status of the worktree, seconds of git in a large repository. (bin/ isn't skipped: scripts live there too.)
        return !relative.EndsWith(".lock", StringComparison.Ordinal)
            && !relative.StartsWith("obj/", StringComparison.Ordinal) && !relative.Contains("/obj/", StringComparison.Ordinal);
    }

    private Task WatcherReadAsync() => RefreshSafeAsync(fromWatcher: true);

    private async Task RefreshSafeAsync(bool fromWatcher = false)
    {
        try
        {
            await ReadStatusAsync(fromWatcher, _lifetime.Token).ConfigureAwait(false);
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
            // A command stopped by its token (Git Changes' Cancel) may have changed something before git was killed.
            await RefreshAsync(cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
        }
    }

    private string Relative(string path) => Path.GetRelativePath(Root!, Real(path)).Replace('\\', '/');

    /// <summary>A file's path relative to the repository, with <c>/</c>, as patches and git's specs name it.</summary>
    public string RelativePath(string path) => Relative(path);

    /// <summary>Git reports real paths (symbolic links resolved); callers may pass another spelling of the same file.</summary>
    private static string Real(string path)
    {
        if (ResolvedPaths.Value is { } count)
        {
            Interlocked.Increment(ref count.Value);
        }

        return Codalus.Platform.RealPath.Resolve(path);
    }

    /// <summary>Counts the paths resolved in a flow that sets it (tests: each resolve reads every folder on the path).</summary>
    internal static readonly AsyncLocal<System.Runtime.CompilerServices.StrongBox<int>?> ResolvedPaths = new();

    /// <summary>How many status reads finished (tests).</summary>
    internal int StatusReads => Volatile.Read(ref _statusReads);

    private int _statusReads;

    /// <summary>
    /// Paths relative to the repository, resolved on the thread pool: resolving a path reads every folder on its way (a
    /// stat and a readlink each on Linux and macOS), which for thousands of files held the UI thread for seconds.
    /// </summary>
    private Task<List<string>> RelativeAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) =>
        Task.Run(() => paths.Select(Relative).ToList(), cancellationToken);

    /// <summary>
    /// The input of <c>--pathspec-from-file=- --pathspec-file-nul</c> (git 2.26, ADR 0008 asks for 2.30): any number of
    /// paths, where on the command line a few thousand passed Windows' limit of 32,767 characters and git didn't start.
    /// </summary>
    private static string Pathspecs(IEnumerable<string> relative) => string.Concat(relative.Select(p => p + "\0"));

    private static readonly string[] s_pathspecsFromInput = ["--pathspec-from-file=-", "--pathspec-file-nul"];

    /// <summary>
    /// <paramref name="paths"/> in parts whose command line fits Windows' limit (32,767 characters), for the commands
    /// that take no pathspec file (<c>git clean</c>).
    /// </summary>
    internal static IEnumerable<IReadOnlyList<string>> CommandLineParts(IReadOnlyList<string> paths, int limit = 24_000)
    {
        var part = new List<string>();
        var length = 0;
        foreach (var path in paths)
        {
            if (part.Count > 0 && length + path.Length + 3 > limit)
            {
                yield return part;
                part = [];
                length = 0;
            }

            part.Add(path);
            length += path.Length + 3;
        }

        if (part.Count > 0)
        {
            yield return part;
        }
    }

    // ------------------------------------------------------------------ staging

    /// <summary>Stages files, by any spelling of their paths (each resolved off the caller's thread).</summary>
    public async Task StageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        await StageRelativeAsync(await RelativeAsync(paths, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    /// <summary>Stages files of the status, by the paths git gave them: nothing to resolve (Git Changes' Stage All).</summary>
    public Task StageAsync(IReadOnlyList<GitFileStatus> files, CancellationToken cancellationToken = default) =>
        StageRelativeAsync([.. files.Select(f => f.RelativePath)], cancellationToken);

    // No paths stage nothing: git add --all with no pathspec would stage the whole worktree.
    private Task StageRelativeAsync(List<string> relative, CancellationToken cancellationToken) => relative.Count == 0
        ? Task.CompletedTask
        : MutateAsync(["add", "--all", .. s_pathspecsFromInput], cancellationToken, Pathspecs(relative));

    /// <summary>Unstages files (before the first commit, removes them from the index), by any spelling of their paths.</summary>
    public async Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default) =>
        await UnstageRelativeAsync(await RelativeAsync(paths, cancellationToken).ConfigureAwait(false), Status?.Head is null, cancellationToken).ConfigureAwait(false);

    /// <summary>Unstages files of the status, by the paths git gave them (Git Changes' Unstage All).</summary>
    public Task UnstageAsync(IReadOnlyList<GitFileStatus> files, CancellationToken cancellationToken = default) =>
        UnstageRelativeAsync([.. files.Select(f => f.RelativePath)], Status?.Head is null, cancellationToken);

    private Task UnstageRelativeAsync(List<string> relative, bool unborn, CancellationToken cancellationToken) => relative.Count == 0
        ? Task.CompletedTask
        : unborn
            ? MutateAsync(["rm", "--cached", "-r", "--quiet", .. s_pathspecsFromInput], cancellationToken, Pathspecs(relative))
            : MutateAsync(["restore", "--staged", .. s_pathspecsFromInput], cancellationToken, Pathspecs(relative));

    /// <summary>
    /// Discards work-tree changes: tracked files go back to the index version; untracked files are deleted. The paths
    /// are sorted on the thread pool, each resolved once (it was once for every untracked file before it: minutes for a
    /// folder of 10,000).
    /// </summary>
    public async Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        var files = Status?.Files ?? [];
        var root = Root ?? throw new InvalidOperationException("No git repository is open.");
        var (tracked, untracked) = await Task.Run(() =>
        {
            var untrackedPaths = files.Where(f => f.IsUntracked).Select(f => f.Path).ToHashSet(Workbench.Projects.SolutionService.PathComparer);
            List<string> trackedFiles = [], untrackedFiles = [];
            foreach (var path in paths)
            {
                var real = Real(path);
                (untrackedPaths.Contains(real) ? untrackedFiles : trackedFiles).Add(Path.GetRelativePath(root, real).Replace('\\', '/'));
            }

            return (trackedFiles, untrackedFiles);
        }, cancellationToken).ConfigureAwait(false);
        await DiscardRelativeAsync(tracked, untracked, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Discards work-tree changes of files of the status, by the paths git gave them (Git Changes' Discard on a folder).</summary>
    public Task DiscardAsync(IReadOnlyList<GitFileStatus> files, CancellationToken cancellationToken = default) =>
        DiscardRelativeAsync([.. files.Where(f => !f.IsUntracked).Select(f => f.RelativePath)], [.. files.Where(f => f.IsUntracked).Select(f => f.RelativePath)], cancellationToken);

    private async Task DiscardRelativeAsync(List<string> tracked, List<string> untracked, CancellationToken cancellationToken)
    {
        try
        {
            if (tracked.Count > 0)
            {
                await GitAsync(["restore", "--worktree", .. s_pathspecsFromInput], cancellationToken, Pathspecs(tracked)).ConfigureAwait(false);
            }

            // git clean takes no pathspec file. A part that fails leaves the parts before it deleted: the status shows what's left.
            foreach (var part in CommandLineParts(untracked))
            {
                await GitAsync(["clean", "-f", "-q", "--", .. part], cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await RefreshAsync(cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The diff of a file: work tree against the index, or (<paramref name="staged"/>) the index against HEAD.</summary>
    public async Task<FileDiff?> DiffAsync(string path, bool staged, CancellationToken cancellationToken = default)
    {
        // Resolved once, not once for each file the status lists before it (every diff reloads after every status read).
        var real = Real(path);
        var status = Status?.Files.FirstOrDefault(f => Workbench.Projects.SolutionService.PathComparer.Equals(f.Path, real));
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

    /// <summary>A file's bytes at <paramref name="revision"/> (<c>:</c> = the index), unchanged; null when it isn't there.</summary>
    public async Task<byte[]?> ShowBytesAsync(string path, string revision, CancellationToken cancellationToken = default)
    {
        if (Root is null)
        {
            return null;
        }

        var spec = revision == ":" ? ":" + Relative(path) : $"{revision}:{Relative(path)}";
        var (code, output, _) = await RunBytesAsync(["show", spec], null, readOnly: true, cancellationToken).ConfigureAwait(false);
        return code == 0 ? output : null;
    }

    /// <summary>
    /// Applies a patch made of bytes (the diff document's rows, #86): to the index (<paramref name="cached"/>) or the
    /// working tree, in reverse to take changes out. Throws git's message when it doesn't apply.
    /// </summary>
    public async Task ApplyPatchAsync(byte[] patch, bool cached, bool reverse, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["apply", "--whitespace=nowarn"];
        if (cached)
        {
            arguments.Add("--cached");
        }

        if (reverse)
        {
            arguments.Add("-R");
        }

        arguments.Add("-");
        try
        {
            var (code, _, error) = await RunBytesAsync(arguments, patch, readOnly: false, cancellationToken).ConfigureAwait(false);
            if (code != 0)
            {
                throw new GitException(error.Trim().Length > 0 ? error.Trim() : $"git apply failed (exit code {code.ToString(CultureInfo.InvariantCulture)}).", code);
            }
        }
        finally
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
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

    public Task FetchAsync(CancellationToken cancellationToken = default) =>
        WithFetchGateAsync(() => MutateAsync(["fetch", "--prune"], cancellationToken), cancellationToken);

    public Task PullAsync(CancellationToken cancellationToken = default) =>
        WithFetchGateAsync(() => MutateAsync(["pull"], cancellationToken), cancellationToken);

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
        _siblingsGate.Dispose();
        FetchGate.Dispose();
    }
}
