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
        var probe = new StartProbe(returned);
        var run = _git.RunAsync(Repo, ["--version"], Ct, environment: probe);
        returned.Set();
        var (code, output, _) = await run;
        Assert.Equal(0, code);
        Assert.StartsWith("git version", output, StringComparison.Ordinal);
        Assert.True(probe.CallerHadReturned, "git started on the caller's thread, before RunAsync returned");
    }

    /// <summary>Variables for a git process that, when read, wait for the caller of RunAsync to have returned (5 s at most).</summary>
    private sealed class StartProbe(ManualResetEventSlim returned) : IReadOnlyDictionary<string, string>
    {
        public bool CallerHadReturned { get; private set; }

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
            CallerHadReturned = returned.Wait(TimeSpan.FromSeconds(5));
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
