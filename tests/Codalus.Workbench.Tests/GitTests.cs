using System.Diagnostics;
using Codalus.Workbench.Git;

namespace Codalus.Workbench.Tests;

/// <summary>The git CLI service (ADR 0008) against real temporary repositories.</summary>
public sealed class GitServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-git-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new(watch: false);

    private string Repo => Path.Combine(_root, "repo");

    private string File1 => Path.Combine(Repo, "a.txt");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] s_lines = ["one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Repo);
        await Git(Repo, "init", "-q", "-b", "main");
        await Configure(Repo);
        await File.WriteAllTextAsync(File1, string.Join('\n', s_lines) + "\n", Ct);
        await Git(Repo, "add", ".");
        await Git(Repo, "commit", "-q", "-m", "initial");
        Assert.True(await _git.OpenAsync(Repo, Ct));
    }

    public ValueTask DisposeAsync()
    {
        _git.Dispose();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal); // git objects are read-only
        }

        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// git starts on the thread pool, never on the caller's thread, which is often the UI thread (Sync, Git Changes): a
    /// start can take long (CreateProcess on Windows, a fork on Linux). Here the start, reading the variables it's given,
    /// waits for the call to have returned, which a start on the caller's thread can't see.
    /// </summary>
    [Fact]
    public async Task Git_starts_off_the_callers_thread()
    {
        using var returned = new ManualResetEventSlim();
        var callerHadReturned = false;
        var run = _git.RunAsync(Repo, ["--version"], Ct, environment: new StartProbe(() => callerHadReturned = returned.Wait(TimeSpan.FromSeconds(5))));
        returned.Set();
        var (code, output, _) = await run;
        Assert.Equal(0, code);
        Assert.StartsWith("git version", output, StringComparison.Ordinal);
        Assert.True(callerHadReturned, "git started on the caller's thread, before RunAsync returned");
    }

    /// <summary>
    /// A write that finds the index locked tries again for a moment: on Windows the antivirus can hold the last command's
    /// <c>index.lock</c> after git deleted it (the command sweep's Stage File). A lock that stays is still an error.
    /// </summary>
    [Fact]
    public async Task A_write_that_finds_the_index_locked_tries_again_until_the_lock_is_gone()
    {
        await File.AppendAllTextAsync(File1, "thirteen\n", Ct);
        var lockFile = Path.Combine(Repo, ".git", "index.lock");
        await File.WriteAllTextAsync(lockFile, string.Empty, Ct);
        var stage = _git.StageAsync([File1], Ct);
        await Task.Delay(150, Ct);
        File.Delete(lockFile);
        await stage;
        var (_, staged, _) = await _git.RunAsync(Repo, ["diff", "--cached", "--name-only"], Ct);
        Assert.Equal("a.txt", staged.Trim());

        await File.AppendAllTextAsync(File1, "fourteen\n", Ct);
        await File.WriteAllTextAsync(lockFile, string.Empty, Ct);
        try
        {
            var error = await Assert.ThrowsAsync<GitException>(() => _git.StageAsync([File1], Ct));
            Assert.Contains("index.lock", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(lockFile);
        }
    }

    /// <summary>
    /// A working-tree diff of a file whose timestamps changed but not its content leaves the index alone: git diff
    /// would refresh it under index.lock even with GIT_OPTIONAL_LOCKS=0, and a diff document reloading on a status change
    /// then held the lock while Stage Active File ran (the Windows command sweep, run 39937763661).
    /// </summary>
    [Fact]
    public async Task A_working_tree_diff_never_writes_the_index()
    {
        File.SetLastWriteTimeUtc(File1, DateTime.UtcNow.AddHours(-1));
        var index = Path.Combine(Repo, ".git", "index");
        var before = await File.ReadAllBytesAsync(index, Ct);
        var written = File.GetLastWriteTimeUtc(index);

        Assert.Null(await _git.DiffAsync(File1, staged: false, Ct));
        Assert.Equal(before, await File.ReadAllBytesAsync(index, Ct));
        Assert.Equal(written, File.GetLastWriteTimeUtc(index));

        await File.AppendAllTextAsync(File1, "thirteen\n", Ct);
        Assert.Equal("thirteen", Assert.Single((await _git.DiffAsync(File1, staged: false, Ct))!.Hunks).Lines.Single(l => l.Kind == '+').Text);
        Assert.Equal(before, await File.ReadAllBytesAsync(index, Ct));
    }

    [Fact]
    public void Only_an_existing_lock_file_is_a_busy_lock()
    {
        Assert.True(GitService.IsLockBusy(128, "fatal: Unable to create 'C:/repo/.git/index.lock': File exists.\n\nAnother git process seems to be running in this repository"));
        Assert.True(GitService.IsLockBusy(128, "error: cannot lock ref 'refs/heads/main': Unable to create '/repo/.git/refs/heads/main.lock': File exists."));
        Assert.False(GitService.IsLockBusy(0, "Unable to create '/repo/.git/index.lock': File exists."));
        Assert.False(GitService.IsLockBusy(1, "error: pathspec 'b.txt' did not match any file(s) known to git"));
    }

    [Fact]
    public void Writes_under_obj_and_inside_git_but_its_index_and_refs_dont_ask_for_a_status()
    {
        // The C# service writes its state under obj/ after each background pass, so at every pause in typing.
        Assert.True(GitService.ChangesStatus("src/App/Program.cs"));
        Assert.True(GitService.ChangesStatus("src/App/bin/run.sh"));
        Assert.True(GitService.ChangesStatus("src/objects/Model.cs"));
        Assert.True(GitService.ChangesStatus(".git/index"));
        Assert.True(GitService.ChangesStatus(".git/refs/heads/main"));
        Assert.False(GitService.ChangesStatus("src/App/obj/codalus.designtime.json"));
        Assert.False(GitService.ChangesStatus("obj/project.assets.json"));
        Assert.False(GitService.ChangesStatus(".git/objects/ab/cdef"));
        Assert.False(GitService.ChangesStatus(".git/index.lock"));
    }

    [Fact]
    public void A_stash_dropped_below_the_newest_and_packed_refs_ask_for_a_status()
    {
        Assert.True(GitService.ChangesStatus(".git/logs/refs/stash"));
        Assert.True(GitService.ChangesStatus(".git/packed-refs"));
        Assert.True(GitService.ChangesStatus(".git/reftable/tables.list"));
        Assert.False(GitService.ChangesStatus(".git/logs/HEAD"));
        Assert.False(GitService.ChangesStatus(".git/packed-refs.lock"));
    }

    /// <summary>
    /// A status read the watcher asks for raises nothing when nothing changed (M56): every write under bin/ (watched:
    /// scripts live there), a build's outputs, rebuilt Git Changes, the open diffs and the Git Repository document. A changed
    /// file written again, a tag and a stash dropped below the newest still raise StatusChanged: the status is the same, but
    /// a diff, the refs and the stash list aren't. A read asked for (after a command) always raises.
    /// </summary>
    [Fact]
    public async Task A_status_read_the_watcher_asks_for_raises_nothing_when_nothing_changed()
    {
        await File.WriteAllTextAsync(Path.Combine(Repo, ".gitignore"), "bin/\n", Ct);
        await Git(Repo, "add", ".gitignore");
        await Git(Repo, "commit", "-q", "-m", "ignore bin");
        using var git = new GitService();
        Assert.True(await git.OpenAsync(Repo, Ct));
        var status = 0;
        var index = 0;
        git.StatusChanged += (_, _) => Interlocked.Increment(ref status);
        git.IndexChanged += (_, _) => Interlocked.Increment(ref index);

        // Runs a change and waits for the read it asks for; returns how many StatusChanged and IndexChanged it raised.
        async Task<(int Status, int Index)> Read(Func<Task> change)
        {
            // The last change's reads are done: one still running or due would be counted for this one.
            await SettledAsync(git);
            var (reads, statusBefore, indexBefore) = (git.StatusReads, Volatile.Read(ref status), Volatile.Read(ref index));
            await change();
            await WaitUntil(() => git.StatusReads > reads, "the watcher asked for no status read");
            return (Volatile.Read(ref status) - statusBefore, Volatile.Read(ref index) - indexBefore);
        }

        Directory.CreateDirectory(Path.Combine(Repo, "bin"));
        Assert.Equal((0, 0), await Read(() => File.WriteAllTextAsync(Path.Combine(Repo, "bin", "App.dll"), "build output", Ct)));
        Assert.Equal((1, 1), await Read(() => File.AppendAllTextAsync(File1, "thirteen\n", Ct)));
        Assert.Equal((1, 0), await Read(() => File.AppendAllTextAsync(File1, "fourteen\n", Ct)));
        Assert.Equal((1, 0), await Read(() => Git(Repo, "tag", "v1")));
        await Git(Repo, "stash", "push", "-q", "-m", "one");
        await File.AppendAllTextAsync(File1, "fifteen\n", Ct);
        await Git(Repo, "stash", "push", "-q", "-m", "two");
        Assert.Equal(1, (await Read(() => Git(Repo, "stash", "drop", "-q", "stash@{1}"))).Status);

        var before = Volatile.Read(ref status);
        await git.RefreshAsync(Ct);
        Assert.Equal(before + 1, Volatile.Read(ref status));
    }

    /// <summary>
    /// While a build runs, the watcher reads the status at most every 2 s, and the read it put off comes when the build ends,
    /// after the usual 400 ms (M56): each quiet moment of 400 ms in a build's writes was a read of a large repository's whole
    /// status. The build is asked about at each write, so one that ends without saying so leaves no throttle behind.
    /// </summary>
    [Fact]
    public async Task While_a_build_runs_the_watcher_reads_the_status_at_most_every_two_seconds_and_when_it_ends()
    {
        using var git = new GitService();
        Assert.True(await git.OpenAsync(Repo, Ct));
        Directory.CreateDirectory(Path.Combine(Repo, "bin"));
        var building = 1;
        git.IsBuilding = () => Volatile.Read(ref building) == 1;
        await SettledAsync(git);
        var reads = git.StatusReads;
        var started = Stopwatch.StartNew();

        // Writes with pauses longer than the watcher's wait: each pause was a read.
        for (var i = 0; i < 8; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(Repo, "bin", $"App{i}.dll"), "build output", Ct);
            await Task.Delay(500, Ct);
        }

        Assert.InRange(git.StatusReads - reads, 1, (int)(started.Elapsed.TotalSeconds / 2) + 1);

        // A write just after a read waits out the 2 s; the build's end brings its read forward, to the usual 400 ms.
        await SettledAsync(git);
        await git.RefreshAsync(Ct);
        var due = git.ReadDue;
        await File.WriteAllTextAsync(Path.Combine(Repo, "bin", "App8.dll"), "build output", Ct);
        await WaitUntil(() => git.ReadDue != due, "the watcher saw no write");
        var throttled = git.ReadDue;
        var ended = Stopwatch.GetTimestamp();
        Volatile.Write(ref building, 0);
        git.OnBuildEnded();
        Assert.True(git.ReadDue < throttled, $"the build's end left the read {Stopwatch.GetElapsedTime(ended, throttled).TotalMilliseconds:0} ms away");
        Assert.InRange(Stopwatch.GetElapsedTime(ended, git.ReadDue).TotalMilliseconds, 400, 1000);
        reads = git.StatusReads;
        await WaitUntil(() => git.StatusReads > reads, "the build's end asked for no status read");
        Assert.Equal(9, git.Status!.Files.Count(f => f.IsUntracked));

        // No build: a write just after a read is read once writes pause, as usual.
        await SettledAsync(git);
        await git.RefreshAsync(Ct);
        due = git.ReadDue;
        var written = Stopwatch.GetTimestamp();
        await File.WriteAllTextAsync(Path.Combine(Repo, "bin", "App9.dll"), "build output", Ct);
        await WaitUntil(() => git.ReadDue != due, "the watcher saw no write");
        Assert.InRange(Stopwatch.GetElapsedTime(written, git.ReadDue).TotalMilliseconds, 400, 1000);
    }

    /// <summary>
    /// A status read that fails or is canceled (a command's refresh, stopped by its token) leaves what the watcher saw to the
    /// next read (M56): a tag changes no status, and the watcher's read that found the status as it was raised nothing for
    /// it, so the Git Repository document and the stash list missed it.
    /// </summary>
    [Fact]
    public async Task A_canceled_status_read_leaves_what_the_watcher_saw_to_the_next()
    {
        using var git = new GitService();
        Assert.True(await git.OpenAsync(Repo, Ct));
        var status = 0;
        git.StatusChanged += (_, _) => Interlocked.Increment(ref status);
        await SettledAsync(git);
        var due = git.ReadDue;
        await Git(Repo, "tag", "v1");
        await WaitUntil(() => git.ReadDue != due, "the watcher didn't see the tag");
        var (reads, before) = (git.StatusReads, Volatile.Read(ref status));

        // A read canceled as git starts takes what the watcher saw, before the watcher's own read (400 ms after the tag).
        using var cancel = new CancellationTokenSource();
        git.AskpassEnvironment = new StartProbe(cancel.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => git.RefreshAsync(cancel.Token));
        git.AskpassEnvironment = null;

        await WaitUntil(() => git.StatusReads > reads, "the watcher asked for no status read");
        Assert.Equal(before + 1, Volatile.Read(ref status));
    }

    /// <summary>
    /// Stage, unstage and discard on files of the status (Git Changes' Stage All, Unstage All, Discard on a folder) resolve
    /// no path, and give git the paths on its input (M56): resolving each path on the UI thread took seconds for thousands
    /// of files, Discard resolved each again for every untracked file (minutes for 10,000), and their paths alone passed
    /// Windows' command line of 32,767 characters, where git didn't start ("git was not found").
    /// </summary>
    [Fact]
    public async Task Stage_unstage_and_discard_take_the_status_paths_on_gits_input()
    {
        var names = Enumerable.Range(0, 1500).Select(i => $"generated files/a file with a long name, spaces and ünïcødé {i:0000}.txt").ToList();
        Directory.CreateDirectory(Path.Combine(Repo, "generated files"));
        foreach (var name in names)
        {
            await File.WriteAllTextAsync(Path.Combine(Repo, name), name, Ct);
        }

        await File.AppendAllTextAsync(File1, "thirteen\n", Ct);
        await _git.RefreshAsync(Ct);
        Assert.Equal(1501, _git.Status!.Unstaged.Count());

        var resolved = new System.Runtime.CompilerServices.StrongBox<int>();
        GitService.ResolvedPaths.Value = resolved;
        await _git.StageAsync([.. _git.Status!.Unstaged], Ct);
        Assert.Equal(1501, _git.Status!.Staged.Count());
        Assert.Equal(names.Order(StringComparer.Ordinal), (await Git(Repo, "diff", "--cached", "--name-only", "-z", "--", "generated files")).Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        await _git.UnstageAsync([.. _git.Status!.Staged], Ct);
        Assert.Empty(_git.Status!.Staged);
        await _git.DiscardAsync([.. _git.Status!.Unstaged], Ct);
        Assert.Equal(0, resolved.Value);
        Assert.Empty(_git.Status!.Files);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(Repo, "generated files")));

        // Paths of any spelling are each resolved once, off the caller's thread.
        foreach (var name in names.Take(300))
        {
            await File.WriteAllTextAsync(Path.Combine(Repo, name), name, Ct);
        }

        await File.AppendAllTextAsync(File1, "fourteen\n", Ct);
        await _git.RefreshAsync(Ct);
        List<string> paths = [File1, .. names.Take(300).Select(n => Path.Combine(Repo, n))];
        await _git.DiscardAsync(paths, Ct);
        Assert.Equal(301, resolved.Value);
        Assert.Empty(_git.Status!.Files);
        Assert.Equal(string.Join('\n', s_lines) + "\n", await File.ReadAllTextAsync(File1, Ct));
    }

    [Fact]
    public void Paths_for_a_command_without_a_pathspec_file_go_in_parts_that_fit_a_command_line()
    {
        var paths = Enumerable.Range(0, 2000).Select(i => $"folder/file {i:0000} with a long name.txt").ToList();
        var parts = GitService.CommandLineParts(paths, 10_000).ToList();
        Assert.True(parts.Count > 1);
        Assert.All(parts, part => Assert.True(part.Sum(p => p.Length + 3) <= 10_000));
        Assert.Equal(paths, parts.SelectMany(p => p));
        Assert.Empty(GitService.CommandLineParts([]));
    }

    /// <summary>
    /// Git LFS lookups by path (Git Changes looks up every changed file) use an index of each list, which a copy of the status
    /// with other locks doesn't share; the same files and locks again are the same state (the window isn't refreshed again).
    /// </summary>
    [Fact]
    public void Lfs_files_and_locks_are_looked_up_by_path_and_compared_by_content()
    {
        var files = Enumerable.Range(0, 20_000).Select(i => new GitLfsFile($"/repo/assets/{i}.png", $"assets/{i}.png", IsPointer: i % 2 == 0, Size: i)).ToList();
        var lfs = new GitLfsStatus(true, true, [.. files, files[7] with { Size = -1 }], [new GitLfsLock("1", "/repo/assets/3.png", "assets/3.png", "ada", null)], null);
        Assert.Equal(files[7], lfs.FileOf("/repo/assets/7.png"));
        Assert.Null(lfs.FileOf("/repo/assets/none.png"));
        Assert.Equal("ada", lfs.LockOf("/repo/assets/3.png")?.Owner);
        Assert.Null(GitLfsStatus.None.FileOf("/repo/assets/7.png"));

        var relocked = lfs with { Locks = [new GitLfsLock("2", "/repo/assets/4.png", "assets/4.png", "grace", null)] };
        Assert.Null(relocked.LockOf("/repo/assets/3.png"));
        Assert.Equal("grace", relocked.LockOf("/repo/assets/4.png")?.Owner);
        Assert.Equal(files[9], relocked.FileOf("/repo/assets/9.png"));

        Assert.True(lfs.IsSameAs(new GitLfsStatus(true, true, [.. lfs.Files], [.. lfs.Locks], null)));
        Assert.False(lfs.IsSameAs(relocked));
        Assert.False(lfs.IsSameAs(lfs with { IsInstalled = false }));
    }

    /// <summary>
    /// Git LFS lookups by path read each list once, to index it (M56): Git Changes looks up every changed file, and a scan of
    /// the LFS files for each, most of which find none, was 200 million comparisons for 10,000 changes among 20,000 assets.
    /// </summary>
    [Fact]
    public void Lfs_lookups_read_each_list_once_whatever_the_number_of_lookups()
    {
        var files = new CountingList<GitLfsFile>([.. Enumerable.Range(0, 20_000).Select(i => new GitLfsFile($"/repo/assets/{i}.png", $"assets/{i}.png", IsPointer: false, Size: i))]);
        var locks = new CountingList<GitLfsLock>([.. Enumerable.Range(0, 1_000).Select(i => new GitLfsLock($"{i}", $"/repo/assets/{i}.png", $"assets/{i}.png", "ada", null))]);
        var lfs = new GitLfsStatus(true, true, files, locks, null);
        var (found, locked) = (0, 0);
        for (var i = 0; i < 10_000; i++)
        {
            // Changed sources, and an asset among each hundred.
            var path = i % 100 == 0 ? $"/repo/assets/{i}.png" : $"/repo/src/{i}.cs";
            found += lfs.FileOf(path) is null ? 0 : 1;
            locked += lfs.LockOf(path) is null ? 0 : 1;
        }

        Assert.Equal((100, 10), (found, locked));
        Assert.Equal(20_000, files.Reads);
        Assert.Equal(1_000, locks.Reads);
    }

    /// <summary>A list that counts the items read from it.</summary>
    private sealed class CountingList<T>(IReadOnlyList<T> items) : IReadOnlyList<T>
    {
        public int Reads { get; private set; }

        public int Count => items.Count;

        public T this[int index]
        {
            get
            {
                Reads++;
                return items[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            foreach (var item in items)
            {
                Reads++;
                yield return item;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// A partial stash too long for Windows' command line is refused there before git runs: git stash gives its paths to the
    /// commands it starts on their command lines, with 12 characters of magic each, and past the limit it stored the stash,
    /// then couldn't start them (an error, a new stash, and the changes still there). Elsewhere it takes only its files.
    /// </summary>
    [Fact]
    public async Task A_partial_stash_too_long_for_a_windows_command_line_is_refused_there_before_git_runs()
    {
        var names = Enumerable.Range(0, 1500).Select(i => $"generated files/a file with a long name, spaces and ünïcødé {i:0000}.txt").ToList();
        Directory.CreateDirectory(Path.Combine(Repo, "generated files"));
        foreach (var name in names)
        {
            await File.WriteAllTextAsync(Path.Combine(Repo, name), name, Ct);
        }

        await File.AppendAllTextAsync(File1, "thirteen\n", Ct);
        await _git.RefreshAsync(Ct);
        Assert.True(GitService.StashFitsCommandLine([.. names.Take(300)]));
        Assert.False(GitService.StashFitsCommandLine(names));

        List<string> paths = [.. names.Select(n => Path.Combine(Repo, n))];
        if (OperatingSystem.IsWindows())
        {
            var refused = await Assert.ThrowsAsync<GitException>(() => _git.StashAsync("generated", includeUntracked: true, paths, Ct));
            Assert.StartsWith("1500 files are too many to stash on their own on Windows", refused.Message, StringComparison.Ordinal);
            Assert.Empty(await _git.StashesAsync(Ct));
            Assert.Equal(1501, _git.Status!.Files.Count);
            return;
        }

        await _git.StashAsync("generated", includeUntracked: true, paths, Ct);
        Assert.Single(await _git.StashesAsync(Ct));
        Assert.DoesNotContain(paths, File.Exists);
        Assert.Equal(["a.txt"], _git.Status!.Files.Select(f => f.RelativePath));
    }

    /// <summary>Waits until no status read is running, or due for writes the watcher saw.</summary>
    private static Task SettledAsync(GitService git) => WaitUntil(() => !git.IsReadPending, "the status reads didn't settle");

    private static async Task WaitUntil(Func<bool> condition, string failure)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, failure);
            await Task.Delay(20, Ct);
        }
    }

    /// <summary>Variables for a git process that, when read as git starts, run <paramref name="started"/> first.</summary>
    private sealed class StartProbe(Action started) : IReadOnlyDictionary<string, string>
    {
        public int Count => 0;

        public IEnumerable<string> Keys => [];

        public IEnumerable<string> Values => [];

        public string this[string key] => throw new KeyNotFoundException(key);

        public bool ContainsKey(string key) => false;

        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string value)
        {
            value = null;
            return false;
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            started();
            return Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static async Task Configure(string repo)
    {
        await Git(repo, "config", "user.name", "Ada Tester");
        await Git(repo, "config", "user.email", "ada@example.com");
        await Git(repo, "config", "commit.gpgsign", "false");
        await Git(repo, "config", "core.autocrlf", "false");
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(Ct);
        var error = await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }

    /// <summary>Runs git where it may fail (a rebase or merge that stops on a conflict): its exit code.</summary>
    private static async Task<int> GitExitCode(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["GIT_EDITOR"] = "true";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        await Task.WhenAll(output, error);
        return process.ExitCode;
    }

    private async Task Edit(params (int Line, string Text)[] changes)
    {
        var lines = (await File.ReadAllTextAsync(File1, Ct)).TrimEnd('\n').Split('\n');
        foreach (var (line, text) in changes)
        {
            lines[line - 1] = text;
        }

        await File.WriteAllTextAsync(File1, string.Join('\n', lines) + "\n", Ct);
    }

    [Fact]
    public async Task Status_hunks_per_hunk_staging_and_discard()
    {
        await Edit((2, "TWO"), (11, "ELEVEN"));
        await File.WriteAllTextAsync(Path.Combine(Repo, "b.txt"), "new\n", Ct);
        await _git.RefreshAsync(Ct);

        var status = _git.Status!;
        Assert.Equal("main", status.Branch);
        Assert.Contains(status.Unstaged, f => f is { RelativePath: "a.txt", WorkTree: 'M' });
        Assert.Contains(status.Unstaged, f => f is { RelativePath: "b.txt", IsUntracked: true });
        Assert.Empty(status.Staged);

        var diff = (await _git.DiffAsync(File1, staged: false, Ct))!;
        Assert.Equal(2, diff.Hunks.Count);
        Assert.Contains(diff.Hunks[0].Lines, l => l is { Kind: '+', Text: "TWO" });

        await _git.StageHunkAsync(diff, diff.Hunks[0], Ct);
        Assert.Contains(_git.Status!.Staged, f => f.RelativePath == "a.txt");
        var staged = (await _git.DiffAsync(File1, staged: true, Ct))!;
        Assert.Single(staged.Hunks);
        Assert.Contains(staged.Hunks[0].Lines, l => l is { Kind: '+', Text: "TWO" });
        Assert.Single((await _git.DiffAsync(File1, staged: false, Ct))!.Hunks);
        Assert.Contains("TWO", await _git.ShowAsync(File1, ":", Ct), StringComparison.Ordinal);
        Assert.DoesNotContain("TWO", await _git.ShowAsync(File1, "HEAD", Ct), StringComparison.Ordinal);

        await _git.UnstageHunkAsync(staged, staged.Hunks[0], Ct);
        Assert.Empty(_git.Status!.Staged);

        diff = (await _git.DiffAsync(File1, staged: false, Ct))!;
        await _git.DiscardHunkAsync(diff, diff.Hunks[1], Ct);
        var text = await File.ReadAllTextAsync(File1, Ct);
        Assert.Contains("TWO", text, StringComparison.Ordinal);
        Assert.Contains("eleven", text, StringComparison.Ordinal);

        var untracked = (await _git.DiffAsync(Path.Combine(Repo, "b.txt"), staged: false, Ct))!;
        Assert.Contains(untracked.Hunks.Single().Lines, l => l is { Kind: '+', Text: "new" });

        await _git.DiscardAsync([File1, Path.Combine(Repo, "b.txt")], Ct);
        Assert.Empty(_git.Status!.Files);
        Assert.False(File.Exists(Path.Combine(Repo, "b.txt")));
    }

    [Fact]
    public async Task Commit_amend_branches_and_merge_conflicts()
    {
        await Edit((1, "ONE"));
        await _git.StageAsync([File1], Ct);
        await _git.CommitAsync("Change one\n\nDetails.", amend: false, all: false, Ct);
        Assert.Empty(_git.Status!.Files);
        Assert.Equal("Change one\n\nDetails.", await _git.LastCommitMessageAsync(Ct));

        await _git.CommitAsync("Change 1", amend: true, all: false, Ct);
        Assert.Equal("Change 1", await _git.LastCommitMessageAsync(Ct));
        Assert.Equal("2", (await Git(Repo, "rev-list", "--count", "HEAD")).Trim());
        await Assert.ThrowsAsync<GitException>(() => _git.CommitAsync("  ", amend: false, all: false, Ct));

        await _git.CreateBranchAsync("feature", switchTo: true, Ct);
        Assert.Equal("feature", _git.Status!.Branch);
        await Edit((5, "FIVE from feature"));
        await _git.CommitAsync("Feature", amend: false, all: true, Ct);

        var main = (await _git.BranchesAsync(Ct)).Single(b => b.Name == "main");
        await _git.SwitchAsync(main, Ct);
        Assert.Equal("main", _git.Status!.Branch);
        await Edit((5, "FIVE from main"));
        await _git.CommitAsync("Main", amend: false, all: true, Ct);

        await Assert.ThrowsAsync<GitException>(() => _git.MergeAsync("feature", Ct));
        Assert.True(_git.Status!.IsMerging);
        Assert.Single(_git.Status.Conflicts);
        await _git.AbortMergeAsync(Ct);
        Assert.False(_git.Status!.IsMerging);
        Assert.Empty(_git.Status.Files);
    }

    [Fact]
    public async Task Push_fetch_and_pull_with_a_remote()
    {
        var bare = Path.Combine(_root, "remote.git");
        await Git(_root, "init", "-q", "--bare", "-b", "main", bare);
        await Git(Repo, "remote", "add", "origin", bare);
        await _git.PushAsync(Ct); // first push: sets the upstream
        Assert.Equal("origin/main", _git.Status!.Upstream);

        var other = Path.Combine(_root, "other");
        await Git(_root, "clone", "-q", bare, other);
        await Configure(other);
        await File.WriteAllTextAsync(Path.Combine(other, "c.txt"), "from the other clone\n", Ct);
        await Git(other, "add", ".");
        await Git(other, "commit", "-q", "-m", "other");
        await Git(other, "push", "-q");

        await _git.FetchAsync(Ct);
        Assert.Equal(1, _git.Status!.Behind);
        await _git.PullAsync(Ct);
        Assert.Equal(0, _git.Status!.Behind);
        Assert.True(File.Exists(Path.Combine(Repo, "c.txt")));

        await Edit((3, "THREE"));
        await _git.CommitAsync("three", amend: false, all: true, Ct);
        Assert.Equal(1, _git.Status!.Ahead);
        await _git.PushAsync(Ct);
        Assert.Equal(0, _git.Status!.Ahead);
        Assert.Contains(await _git.BranchesAsync(Ct), b => b is { Name: "origin/main", IsRemote: true });
    }

    /// <summary>
    /// A bare remote with main and feature, both tracked here, and another clone of it that commits to both (the first
    /// commit to <paramref name="file"/>). Returns the other clone and its new feature commit.
    /// </summary>
    private async Task<(string Other, string FeatureTip)> RemoteWithNewCommitsAsync(string file = "c.txt")
    {
        var bare = Path.Combine(_root, "remote.git");
        await Git(_root, "init", "-q", "--bare", "-b", "main", bare);
        await Git(Repo, "remote", "add", "origin", bare);
        await Git(Repo, "push", "-q", "-u", "origin", "main");
        await Git(Repo, "branch", "feature");
        await Git(Repo, "push", "-q", "-u", "origin", "feature");

        var other = Path.Combine(_root, "other");
        await Git(_root, "clone", "-q", bare, other);
        await Configure(other);
        await File.WriteAllTextAsync(Path.Combine(other, file), "main from the other clone\n", Ct);
        await Git(other, "add", ".");
        await Git(other, "commit", "-q", "-m", "main from the other clone");
        await Git(other, "push", "-q", "origin", "main");
        await Git(other, "switch", "-q", "feature");
        await File.WriteAllTextAsync(Path.Combine(other, "feature.txt"), "feature from the other clone\n", Ct);
        await Git(other, "add", ".");
        await Git(other, "commit", "-q", "-m", "feature from the other clone");
        await Git(other, "push", "-q", "origin", "feature");
        return (other, (await Git(other, "rev-parse", "HEAD")).Trim());
    }

    /// <summary>
    /// Update Branch fast-forwards a branch that isn't checked out to its upstream (fetching only that branch), and leaves
    /// HEAD, the working tree and the other branches alone; the current branch is fast-forwarded with its files.
    /// </summary>
    [Fact]
    public async Task Update_branch_fast_forwards_a_branch_without_checking_it_out()
    {
        var (_, featureTip) = await RemoteWithNewCommitsAsync();
        var mainBefore = (await Git(Repo, "rev-parse", "main")).Trim();
        var featureBefore = (await Git(Repo, "rev-parse", "feature")).Trim();

        var update = await _git.UpdateBranchAsync("feature", Ct);
        Assert.Equal(new GitBranchUpdate("feature", GitBranchUpdateOutcome.Updated) { Upstream = "origin/feature", Before = featureBefore, After = featureTip, Behind = 1 }, update);
        Assert.Equal(featureTip, (await Git(Repo, "rev-parse", "refs/heads/feature")).Trim());
        Assert.Equal(featureTip, (await Git(Repo, "rev-parse", "refs/remotes/origin/feature")).Trim()); // its remote-tracking branch too
        Assert.Equal("main", _git.Status!.Branch);
        Assert.Equal(mainBefore, (await Git(Repo, "rev-parse", "HEAD")).Trim());
        Assert.False(File.Exists(Path.Combine(Repo, "feature.txt")));
        Assert.Empty(_git.Status.Files);

        // Only feature was fetched: main's remote-tracking branch hasn't moved.
        Assert.Equal(mainBefore, (await Git(Repo, "rev-parse", "refs/remotes/origin/main")).Trim());

        // Again, by its full ref: nothing to do.
        update = await _git.UpdateBranchAsync("refs/heads/feature", Ct);
        Assert.Equal(GitBranchUpdateOutcome.UpToDate, update.Outcome);
        Assert.Equal((featureTip, featureTip), (update.Before, update.After));

        // The current branch: fast-forwarded, its new file in the working tree, nothing left to pull.
        await File.WriteAllTextAsync(Path.Combine(Repo, "notes.txt"), "uncommitted\n", Ct);
        update = await _git.UpdateBranchAsync("main", Ct);
        Assert.Equal((GitBranchUpdateOutcome.Updated, true, 1), (update.Outcome, update.IsCurrent, update.Behind));
        Assert.Equal(update.After, (await Git(Repo, "rev-parse", "HEAD")).Trim());
        Assert.NotEqual(mainBefore, update.After);
        Assert.True(File.Exists(Path.Combine(Repo, "c.txt")));
        Assert.Equal("uncommitted\n", await File.ReadAllTextAsync(Path.Combine(Repo, "notes.txt"), Ct));
        Assert.Equal(("main", 0), (_git.Status!.Branch, _git.Status.Behind));
    }

    /// <summary>
    /// Update Branch never rewrites a branch: one that has diverged from its upstream stays as it is (with both counts), one
    /// with only its own commits has nothing to update, and one with no upstream or checked out in another worktree is
    /// left for the shell to explain.
    /// </summary>
    [Fact]
    public async Task Update_branch_leaves_diverged_unpublished_and_elsewhere_checked_out_branches()
    {
        var (_, featureTip) = await RemoteWithNewCommitsAsync();

        // feature gets a commit of its own here, so it and origin/feature have diverged.
        await Git(Repo, "switch", "-q", "feature");
        await File.WriteAllTextAsync(Path.Combine(Repo, "mine.txt"), "mine\n", Ct);
        await Git(Repo, "add", ".");
        await Git(Repo, "commit", "-q", "-m", "mine");
        var mine = (await Git(Repo, "rev-parse", "HEAD")).Trim();
        await Git(Repo, "switch", "-q", "main");
        await _git.RefreshAsync(Ct);

        var update = await _git.UpdateBranchAsync("feature", Ct);
        Assert.Equal(new GitBranchUpdate("feature", GitBranchUpdateOutcome.Diverged) { Upstream = "origin/feature", Before = mine, After = mine, Ahead = 1, Behind = 1 }, update);
        Assert.Equal(mine, (await Git(Repo, "rev-parse", "refs/heads/feature")).Trim());
        Assert.Equal(featureTip, (await Git(Repo, "rev-parse", "refs/remotes/origin/feature")).Trim()); // fetched all the same

        // The current branch diverged too: no merge commit, nothing changes.
        await File.WriteAllTextAsync(Path.Combine(Repo, "main.txt"), "main here\n", Ct);
        await Git(Repo, "add", ".");
        await Git(Repo, "commit", "-q", "-m", "main here");
        var mainHere = (await Git(Repo, "rev-parse", "HEAD")).Trim();
        await _git.RefreshAsync(Ct);
        update = await _git.UpdateBranchAsync("main", Ct);
        Assert.Equal((GitBranchUpdateOutcome.Diverged, true, 1, 1), (update.Outcome, update.IsCurrent, update.Ahead, update.Behind));
        Assert.Equal(mainHere, (await Git(Repo, "rev-parse", "HEAD")).Trim());
        Assert.False(File.Exists(Path.Combine(Repo, "c.txt")));

        // Ahead of its upstream only: nothing to update (Push sends it), and not rewound.
        await Git(Repo, "branch", "--track", "ahead", "origin/main");
        await Git(Repo, "switch", "-q", "ahead");
        await File.WriteAllTextAsync(Path.Combine(Repo, "ahead.txt"), "ahead\n", Ct);
        await Git(Repo, "add", ".");
        await Git(Repo, "commit", "-q", "-m", "ahead");
        var aheadTip = (await Git(Repo, "rev-parse", "HEAD")).Trim();
        await Git(Repo, "switch", "-q", "main");
        await _git.RefreshAsync(Ct);
        update = await _git.UpdateBranchAsync("ahead", Ct);
        Assert.Equal(new GitBranchUpdate("ahead", GitBranchUpdateOutcome.Ahead) { Upstream = "origin/main", Before = aheadTip, After = aheadTip, Ahead = 1 }, update);
        Assert.Equal(aheadTip, (await Git(Repo, "rev-parse", "refs/heads/ahead")).Trim());

        // No upstream: nothing is fetched.
        await Git(Repo, "branch", "local-only");
        update = await _git.UpdateBranchAsync("local-only", Ct);
        Assert.Equal(GitBranchUpdateOutcome.NoUpstream, update.Outcome);
        Assert.Null(update.Upstream);

        // Checked out in another worktree: that worktree's files would no longer match, so it's left to a pull there.
        var worktree = Path.Combine(_root, "worktree");
        await Git(Repo, "branch", "--track", "elsewhere", "origin/feature");
        await Git(Repo, "branch", "-f", "elsewhere", featureTip + "~1");
        await Git(Repo, "worktree", "add", "-q", worktree, "elsewhere");
        update = await _git.UpdateBranchAsync("elsewhere", Ct);
        Assert.Equal(GitBranchUpdateOutcome.CheckedOutElsewhere, update.Outcome);
        Assert.Equal(Codalus.Platform.RealPath.Resolve(worktree), Codalus.Platform.RealPath.Resolve(update.WorktreePath!));
        Assert.Equal(Path.GetFullPath(update.WorktreePath!), update.WorktreePath); // the OS's separators (git writes C:/ on Windows)
        Assert.Null(update.Operation);
        Assert.Equal((await Git(Repo, "rev-parse", featureTip + "~1")).Trim(), (await Git(Repo, "rev-parse", "refs/heads/elsewhere")).Trim());

        // A name that is only a folder of branches isn't a branch.
        await Git(Repo, "branch", "topic/one");
        var missing = await Assert.ThrowsAsync<GitException>(() => _git.UpdateBranchAsync("topic", Ct));
        Assert.Contains("'topic'", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A rebase detaches HEAD, so git lists the branch being rebased as checked out nowhere, yet it counts as current
    /// (D38, question 21): Update leaves it alone, here and in another worktree, without running git (git 2.35 and later
    /// refuse to fetch into it, older git moved it under the rebase, which then couldn't finish). So does the current
    /// branch during a merge.
    /// </summary>
    [Fact]
    public async Task Update_branch_leaves_a_branch_being_rebased_and_a_merging_current_branch_alone()
    {
        await RemoteWithNewCommitsAsync("a.txt");

        // feature changes a.txt as origin/main does, so rebasing it onto origin/main stops on a conflict.
        await Git(Repo, "switch", "-q", "feature");
        await File.WriteAllTextAsync(File1, "feature's own a.txt\n", Ct);
        await Git(Repo, "commit", "-q", "-am", "feature's a.txt");
        var featureHere = (await Git(Repo, "rev-parse", "HEAD")).Trim();
        await Git(Repo, "fetch", "-q", "origin", "main");
        Assert.NotEqual(0, await GitExitCode(Repo, "rebase", "origin/main"));
        await _git.RefreshAsync(Ct);
        Assert.Equal("rebase", _git.Status!.Operation);
        Assert.Null(_git.Status.Branch); // HEAD is detached
        var log = new List<string>();
        _git.Log += (_, line) => log.Add(line);

        var update = await _git.UpdateBranchAsync("feature", Ct);
        Assert.Equal((GitBranchUpdateOutcome.OperationInProgress, "rebase"), (update.Outcome, update.Operation));
        Assert.Equal(featureHere, (await Git(Repo, "rev-parse", "refs/heads/feature")).Trim());
        Assert.Contains(await _git.BranchOperationsAsync(Ct), o => o is { Branch: "feature", Operation: "rebase", IsThisWorktree: true });
        Assert.DoesNotContain(log, l => l.StartsWith("> git fetch", StringComparison.Ordinal));
        await Git(Repo, "rebase", "--abort");

        // In another worktree: said to be checked out there, and where.
        var worktree = Path.Combine(_root, "worktree");
        await Git(Repo, "switch", "-q", "main");
        await Git(Repo, "worktree", "add", "-q", worktree, "feature");
        await Configure(worktree);
        Assert.NotEqual(0, await GitExitCode(worktree, "rebase", "origin/main"));
        update = await _git.UpdateBranchAsync("feature", Ct);
        Assert.Equal((GitBranchUpdateOutcome.CheckedOutElsewhere, "rebase"), (update.Outcome, update.Operation));
        Assert.Equal(Codalus.Platform.RealPath.Resolve(worktree), Codalus.Platform.RealPath.Resolve(update.WorktreePath!));
        Assert.Equal(featureHere, (await Git(Repo, "rev-parse", "refs/heads/feature")).Trim());
        Assert.DoesNotContain(log, l => l.StartsWith("> git fetch", StringComparison.Ordinal));
        await Git(worktree, "rebase", "--abort");

        // The current branch during a merge: nothing is fetched, nothing moves.
        await Git(Repo, "switch", "-q", "-c", "conflicting");
        await File.WriteAllTextAsync(File1, "conflicting a.txt\n", Ct);
        await Git(Repo, "commit", "-q", "-am", "conflicting a.txt");
        await Git(Repo, "switch", "-q", "main");
        await File.WriteAllTextAsync(File1, "main's own a.txt\n", Ct);
        await Git(Repo, "commit", "-q", "-am", "main's a.txt");
        var mainHere = (await Git(Repo, "rev-parse", "HEAD")).Trim();
        Assert.NotEqual(0, await GitExitCode(Repo, "merge", "--no-edit", "conflicting"));
        await _git.RefreshAsync(Ct);
        update = await _git.UpdateBranchAsync("main", Ct);
        Assert.Equal((GitBranchUpdateOutcome.OperationInProgress, "merge", true), (update.Outcome, update.Operation, update.IsCurrent));
        Assert.Equal(mainHere, (await Git(Repo, "rev-parse", "HEAD")).Trim());
        Assert.DoesNotContain(log, l => l.StartsWith("> git fetch", StringComparison.Ordinal));
    }

    /// <summary>
    /// Updates of two branches at once, one an ancestor of the other, while a full fetch also runs: each ends at its own
    /// upstream's tip (a branch that isn't checked out moves in git's own fetch, never from a FETCH_HEAD another fetch may
    /// have rewritten), and a second Update waits for the first (<see cref="GitService.FetchGate"/>), as does a cancel.
    /// </summary>
    [Fact]
    public async Task Update_branch_runs_one_at_a_time_and_each_branch_gets_its_own_upstream()
    {
        var (other, featureTip) = await RemoteWithNewCommitsAsync();

        // develop: behind origin/develop, and an ancestor of origin/feature's tip.
        await Git(other, "branch", "develop", featureTip + "~1");
        await Git(other, "push", "-q", "origin", "develop");
        await Git(Repo, "fetch", "-q", "origin", "develop");
        await Git(Repo, "branch", "--track", "develop", "origin/develop");
        await Git(other, "switch", "-q", "develop");
        await File.WriteAllTextAsync(Path.Combine(other, "develop.txt"), "develop\n", Ct);
        await Git(other, "add", ".");
        await Git(other, "commit", "-q", "-m", "develop");
        await Git(other, "push", "-q", "origin", "develop");
        var developTip = (await Git(other, "rev-parse", "HEAD")).Trim();

        var develop = _git.UpdateBranchAsync("develop", Ct);
        var feature = _git.UpdateBranchAsync("feature", Ct);
        var fetch = _git.FetchAsync(Ct);
        await Task.WhenAll(develop, feature, fetch);
        Assert.Equal((GitBranchUpdateOutcome.Updated, developTip, 1), ((await develop).Outcome, (await develop).After, (await develop).Behind));
        Assert.Equal((GitBranchUpdateOutcome.Updated, featureTip, 1), ((await feature).Outcome, (await feature).After, (await feature).Behind));
        Assert.Equal(developTip, (await Git(Repo, "rev-parse", "refs/heads/develop")).Trim());
        Assert.Equal(featureTip, (await Git(Repo, "rev-parse", "refs/heads/feature")).Trim());

        // While another FETCH_HEAD writer holds the gate, Update waits; a cancel stops it waiting.
        await _git.FetchGate.WaitAsync(Ct);
        Task<GitBranchUpdate> waiting;
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            waiting = _git.UpdateBranchAsync("feature", cancel.Token);
            await Task.Delay(300, Ct);
            Assert.False(waiting.IsCompleted);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }

        waiting = _git.UpdateBranchAsync("feature", Ct);
        await Task.Delay(300, Ct);
        Assert.False(waiting.IsCompleted);
        _git.FetchGate.Release();
        Assert.Equal(GitBranchUpdateOutcome.UpToDate, (await waiting).Outcome);
    }

    /// <summary>
    /// The current branch's fast-forward is git's <c>merge --ff-only</c>: when it would overwrite a local change, git
    /// refuses and says so, and nothing moves or is lost (no stash, no merge commit).
    /// </summary>
    [Fact]
    public async Task Update_branch_keeps_local_changes_the_current_branch_fast_forward_would_overwrite()
    {
        await RemoteWithNewCommitsAsync("a.txt"); // origin/main changes a.txt
        var mainBefore = (await Git(Repo, "rev-parse", "HEAD")).Trim();
        await File.WriteAllTextAsync(File1, "my uncommitted a.txt\n", Ct);
        await _git.RefreshAsync(Ct);

        var failure = await Assert.ThrowsAsync<GitException>(() => _git.UpdateBranchAsync("main", Ct));
        Assert.Contains("a.txt", failure.Message, StringComparison.Ordinal);
        Assert.Equal(mainBefore, (await Git(Repo, "rev-parse", "HEAD")).Trim());
        Assert.Equal("my uncommitted a.txt\n", await File.ReadAllTextAsync(File1, Ct));
        Assert.Equal(1, _git.Status!.Behind); // fetched, so the count shows what's waiting
    }

    [Fact]
    public async Task Blame_annotates_committed_and_uncommitted_lines()
    {
        var blame = await _git.BlameAsync(File1, contents: null, Ct);
        var range = Assert.Single(blame);
        Assert.Equal((1, 12), (range.StartLine, range.LineCount));
        Assert.Equal("Ada Tester", range.Author);
        Assert.Equal("initial", range.Summary);

        var edited = string.Join('\n', s_lines.Select((l, i) => i == 5 ? "SIX" : l)) + "\n";
        blame = await _git.BlameAsync(File1, edited, Ct);
        Assert.Equal(3, blame.Count);
        Assert.True(blame[1].IsUncommitted);
        Assert.Equal(6, blame[1].StartLine);
    }

    [Fact]
    public async Task Paths_through_a_symbolic_link_reach_the_same_files()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Symbolic links need extra privileges on Windows.");
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, Repo);
        await Edit((4, "FOUR"));
        await _git.RefreshAsync(Ct);

        var viaLink = Path.Combine(link, "a.txt");
        Assert.Single((await _git.DiffAsync(viaLink, staged: false, Ct))!.Hunks);
        await _git.StageAsync([viaLink], Ct);
        Assert.Contains(_git.Status!.Staged, f => f.RelativePath == "a.txt");
    }

    [Fact]
    public async Task Folders_outside_a_repository_have_no_status()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);
        using var git = new GitService(watch: false);
        Assert.False(await git.OpenAsync(plain, Ct) && git.Root!.StartsWith(plain, StringComparison.OrdinalIgnoreCase));
    }
}

public class LineDiffTests
{
    [Fact]
    public void Changes_are_added_modified_and_deleted_runs()
    {
        string[] old = ["a", "b", "c", "d", "e", "f"];
        string[] now = ["a", "B", "c", "x", "y", "d", "f"];
        var changes = LineDiff.Changes(old, now);
        Assert.Equal(
            [new LineChange(LineChangeKind.Modified, 2, 1), new LineChange(LineChangeKind.Added, 4, 2), new LineChange(LineChangeKind.Deleted, 7, 0)],
            changes);
    }

    [Fact]
    public void The_edit_script_reproduces_both_texts()
    {
        var random = new Random(7);
        for (var round = 0; round < 50; round++)
        {
            var old = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(0, 6).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var now = Enumerable.Range(0, random.Next(0, 40)).Select(_ => random.Next(0, 6).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var ops = LineDiff.Diff(old, now);
            Assert.Equal(old, ops.Where(o => o.Kind != '+').Select(o => old[o.OldIndex]));
            Assert.Equal(now, ops.Where(o => o.Kind != '-').Select(o => now[o.NewIndex]));
            Assert.All(ops.Where(o => o.Kind == ' '), o => Assert.Equal(old[o.OldIndex], now[o.NewIndex]));
        }
    }

    [Fact]
    public void Lines_and_hunk_headers()
    {
        Assert.Equal(["a", "b"], LineDiff.Lines("a\r\nb\n"));
        Assert.Empty(LineDiff.Lines(string.Empty));
        Assert.Equal((3, 1, 3, 2), GitParsers.ParseHunkHeader("@@ -3 +3,2 @@ void M()"));
    }

    [Fact]
    public void Porcelain_v2_status_with_renames_and_conflicts()
    {
        var output = "# branch.oid abc\0# branch.head my branch\0# branch.upstream origin/my branch\0# branch.ab +2 -1\0"
                     + "1 .M N... 100644 100644 100644 h h src/a b.cs\0"
                     + "2 R. N... 100644 100644 100644 h h R100 new.cs\0old.cs\0"
                     + "u UU N... 100644 100644 100644 100644 h h h c.cs\0"
                     + "? d.cs\0";
        var status = GitParsers.ParseStatus("/r", output, isMerging: true);
        Assert.Equal(("my branch", "origin/my branch", 2, 1), (status.Branch, status.Upstream, status.Ahead, status.Behind));
        Assert.Equal(["src/a b.cs", "new.cs", "c.cs", "d.cs"], status.Files.Select(f => f.RelativePath));
        Assert.True(status.Files[1].IsStaged);
        Assert.EndsWith("old.cs", status.Files[1].OriginalPath, StringComparison.Ordinal);
        Assert.True(status.Files[2].IsConflict);
        Assert.True(status.Files[3].IsUntracked);
    }
}

public class RealPathTests
{
    [Fact]
    public void Symbolic_links_in_any_component_are_resolved()
    {
        var root = Codalus.Platform.RealPath.Resolve(Path.Combine(Path.GetTempPath(), "codalus-realpath-" + Guid.NewGuid().ToString("N")));
        var target = Directory.CreateDirectory(Path.Combine(root, "target", "inner")).FullName;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(target, Codalus.Platform.RealPath.Resolve(target + Path.DirectorySeparatorChar + "."));
                return;
            }

            Directory.CreateSymbolicLink(Path.Combine(root, "link"), Path.Combine(root, "target"));
            Assert.Equal(Path.Combine(target, "file.cs"), Codalus.Platform.RealPath.Resolve(Path.Combine(root, "link", "inner", "file.cs")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
