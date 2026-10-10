using System.Diagnostics;
using DaedalusStudio.Workbench.Git;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>M13: history and its graph, operations in progress and conflicts, stash, rebase, tags, remotes, per-line staging and repositories.</summary>
public sealed class GitDepthTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(DaedalusStudio.Platform.RealPath.Resolve(Path.GetTempPath()), "daedalus-gitdepth-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new(watch: false);

    private string Repo => Path.Combine(_root, "repo");

    private string FileA => Path.Combine(Repo, "a.txt");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Repo);
        await Git(Repo, "init", "-q", "-b", "main");
        await Configure(Repo);
        await Write(FileA, "one", "two", "three", "four", "five", "six");
        await Commit("initial", "Ada Tester");
        Assert.True(await _git.OpenAsync(Repo, Ct));
    }

    public ValueTask DisposeAsync()
    {
        _git.Dispose();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static async Task Configure(string repo)
    {
        await Git(repo, "config", "user.name", "Ada Tester");
        await Git(repo, "config", "user.email", "ada@example.com");
        await Git(repo, "config", "commit.gpgsign", "false");
        await Git(repo, "config", "core.autocrlf", "false");
        await Git(repo, "config", "protocol.file.allow", "always");
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

    private static Task Write(string path, params string[] lines) => File.WriteAllTextAsync(path, string.Join('\n', lines) + "\n", Ct);

    private async Task Commit(string message, string author = "Ada Tester")
    {
        await Git(Repo, "add", "-A");
        await Git(Repo, "-c", "user.name=" + author, "commit", "-q", "-m", message);
    }

    private async Task<string> Head() => (await Git(Repo, "rev-parse", "HEAD")).Trim();

    private async Task<string[]> Subjects(string range = "HEAD") => (await Git(Repo, "log", "--format=%s", range)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task History_pages_filters_graph_details_and_line_history()
    {
        await Git(Repo, "switch", "-q", "-c", "feature");
        await Write(Path.Combine(Repo, "b.txt"), "b");
        await Commit("add b", "Grace Hopper");
        await Git(Repo, "switch", "-q", "main");
        await Write(FileA, "one", "TWO", "three", "four", "five", "six");
        await Commit("change two");
        await Git(Repo, "merge", "-q", "--no-ff", "--no-edit", "feature");
        await _git.RefreshAsync(Ct);

        var all = await _git.LogAsync(new GitLogQuery { AllBranches = true }, 0, 50, Ct);
        Assert.Equal(4, all.Count);
        var merge = all[0];
        Assert.Equal(2, merge.Parents.Count);
        Assert.Contains(merge.Refs, r => r.Contains("main", StringComparison.Ordinal));

        // The graph: the merge commit has two lines down, and the branch's commit sits in a second lane.
        var graph = new GitGraph();
        graph.Add(all.Take(2));
        graph.Add(all.Skip(2)); // a second page continues the layout
        Assert.Equal(2, merge.Graph!.Bottom.Count);
        Assert.Equal(2, all.Max(c => c.Graph!.LaneCount));
        Assert.Contains(all, c => c.Subject == "add b" && c.Graph!.Lane == 1 || c.Subject == "change two" && c.Graph!.Lane == 1);
        Assert.Equal(0, all[^1].Graph!.Lane);
        Assert.Equal(2, all[^1].Graph!.Top.Count); // both lines end at the root commit

        // Paging and filters.
        Assert.Equal(["change two", "add b"], (await _git.LogAsync(new GitLogQuery { AllBranches = true }, 1, 2, Ct)).Select(c => c.Subject).Order(StringComparer.Ordinal).Reverse());
        Assert.Equal(["add b"], (await _git.LogAsync(new GitLogQuery { AllBranches = true, Author = "grace" }, 0, 50, Ct)).Select(c => c.Subject));
        Assert.Equal(["change two"], (await _git.LogAsync(new GitLogQuery { Message = "TWO" }, 0, 50, Ct)).Select(c => c.Subject));
        Assert.Equal(["change two", "initial"], (await _git.LogAsync(new GitLogQuery { Path = FileA }, 0, 50, Ct)).Select(c => c.Subject));

        // Details: the message and the changed files.
        var details = await _git.CommitDetailsAsync(all.Single(c => c.Subject == "add b").Hash, Ct);
        Assert.Equal("add b", details!.Message);
        Assert.Equal(('A', Path.Combine(Repo, "b.txt")), (details.Files.Single().Status, details.Files.Single().Path));

        // Line history: only the commits that touched lines 2–3.
        Assert.Equal(["change two", "initial"], (await _git.LineHistoryAsync(FileA, 2, 3, Ct)).Select(c => c.Subject));
        Assert.Equal(["initial"], (await _git.LineHistoryAsync(FileA, 5, 6, Ct)).Select(c => c.Subject));
    }

    [Fact]
    public async Task A_merge_conflict_is_resolved_and_continued_and_a_cherry_pick_is_aborted()
    {
        await Git(Repo, "switch", "-q", "-c", "other");
        await Write(FileA, "one", "THEIRS", "three", "four", "five", "six");
        await Commit("theirs");
        var theirs = await Head();
        await Git(Repo, "switch", "-q", "main");
        await Write(FileA, "one", "OURS", "three", "four", "five", "six");
        await Commit("ours");
        await _git.RefreshAsync(Ct);

        await Assert.ThrowsAsync<GitException>(() => _git.MergeAsync("other", Ct));
        Assert.Equal("merge", _git.Status!.Operation);
        Assert.Single(_git.Status.Conflicts);
        var (@base, ours, theirsText) = await _git.ConflictVersionsAsync(FileA, Ct);
        Assert.Contains("two", @base, StringComparison.Ordinal);
        Assert.Contains("OURS", ours, StringComparison.Ordinal);
        Assert.Contains("THEIRS", theirsText, StringComparison.Ordinal);

        await _git.ResolveAsync(FileA, "one\nOURS\nTHEIRS\nthree\nfour\nfive\nsix\n", Ct);
        Assert.Empty(_git.Status!.Conflicts);
        await _git.ContinueAsync(Ct);
        Assert.Null(_git.Status!.Operation);
        Assert.Equal(2, (await _git.LogAsync(new GitLogQuery(), 0, 1, Ct))[0].Parents.Count);

        // A cherry-pick that conflicts, aborted: back where it started.
        await Write(FileA, "one", "MAIN AGAIN", "three", "four", "five", "six");
        await Commit("main again");
        var before = await Head();
        await Assert.ThrowsAsync<GitException>(() => _git.CherryPickAsync(theirs, Ct));
        Assert.Equal("cherry-pick", _git.Status!.Operation);
        await _git.AbortAsync(Ct);
        Assert.Null(_git.Status!.Operation);
        Assert.Equal(before, await Head());

        // Take one side's whole file.
        await Git(Repo, "switch", "-q", "-c", "side", "HEAD~1");
        await Write(FileA, "one", "SIDE", "three", "four", "five", "six");
        await Commit("side");
        await Git(Repo, "switch", "-q", "main");
        await _git.RefreshAsync(Ct);
        await Assert.ThrowsAsync<GitException>(() => _git.MergeAsync("side", Ct));
        await _git.ResolveWithAsync(FileA, ours: false, Ct);
        Assert.Contains("SIDE", await File.ReadAllTextAsync(FileA, Ct), StringComparison.Ordinal);
        await _git.ContinueAsync(Ct);
        Assert.Null(_git.Status!.Operation);
    }

    [Fact]
    public async Task Stash_cherry_pick_revert_reset_and_tags()
    {
        // A partial stash keeps the other file's change.
        var other = Path.Combine(Repo, "c.txt");
        await Write(other, "c");
        await Commit("add c");
        await Write(FileA, "one", "stashed", "three", "four", "five", "six");
        await Write(other, "c changed");
        await _git.RefreshAsync(Ct);
        await _git.StashAsync("wip", includeUntracked: false, [FileA], Ct);
        Assert.DoesNotContain("stashed", await File.ReadAllTextAsync(FileA, Ct), StringComparison.Ordinal);
        Assert.Contains("changed", await File.ReadAllTextAsync(other, Ct), StringComparison.Ordinal);
        var stash = Assert.Single(await _git.StashesAsync(Ct));
        Assert.Contains("wip", stash.Message, StringComparison.Ordinal);
        await _git.ApplyStashAsync(stash, pop: true, Ct);
        Assert.Contains("stashed", await File.ReadAllTextAsync(FileA, Ct), StringComparison.Ordinal);
        Assert.Empty(await _git.StashesAsync(Ct));
        await Git(Repo, "checkout", "--", ".");

        // Cherry-pick from a branch, then revert it.
        await Git(Repo, "switch", "-q", "-c", "topic");
        await Write(Path.Combine(Repo, "d.txt"), "d");
        await Commit("add d");
        var topic = await Head();
        await Git(Repo, "switch", "-q", "main");
        await _git.RefreshAsync(Ct);
        await _git.CherryPickAsync(topic, Ct);
        Assert.True(File.Exists(Path.Combine(Repo, "d.txt")));
        await _git.RevertAsync(await Head(), Ct);
        Assert.False(File.Exists(Path.Combine(Repo, "d.txt")));
        Assert.StartsWith("Revert \"add d\"", (await Subjects())[0], StringComparison.Ordinal);

        // Reset: soft keeps the change staged.
        var revert = await Head();
        await _git.ResetAsync(revert + "~1", "soft", Ct);
        Assert.Contains(_git.Status!.Staged, f => f.Path.EndsWith("d.txt", StringComparison.Ordinal));
        await _git.ResetAsync(revert, "hard", Ct);
        Assert.Empty(_git.Status!.Files);

        // Tags.
        await _git.CreateTagAsync("v1.0", null, "First release", Ct);
        await _git.CreateTagAsync("light", topic, null, Ct);
        var tags = await _git.TagsAsync(Ct);
        Assert.Equal("First release", tags.Single(t => t.Name == "v1.0").Message);
        Assert.Equal(topic, tags.Single(t => t.Name == "light").Commit);
        await _git.DeleteTagAsync("light", Ct);
        Assert.DoesNotContain(await _git.TagsAsync(Ct), t => t.Name == "light");
    }

    [Fact]
    public async Task An_interactive_rebase_reorders_squashes_rewords_and_drops()
    {
        var baseCommit = await Head();
        foreach (var name in new[] { "first", "second", "third", "fourth" })
        {
            await File.WriteAllTextAsync(Path.Combine(Repo, name + ".txt"), name, Ct);
            await Commit("add " + name);
        }

        await _git.RefreshAsync(Ct);
        var steps = await _git.RebaseTodoAsync(baseCommit, Ct);
        Assert.Equal(["add first", "add second", "add third", "add fourth"], steps.Select(s => s.Subject));

        // third before second; second squashed into third; first reworded; fourth dropped.
        await _git.RebaseInteractiveAsync(baseCommit,
        [
            steps[0] with { Action = "reword", Message = "first, reworded" },
            steps[2],
            steps[1] with { Action = "fixup" },
            steps[3] with { Action = "drop" },
        ], Ct);

        Assert.Null(_git.Status!.Operation);
        Assert.Equal(["add third", "first, reworded", "initial"], await Subjects());
        Assert.True(File.Exists(Path.Combine(Repo, "second.txt")));
        Assert.False(File.Exists(Path.Combine(Repo, "fourth.txt")));
    }

    [Fact]
    public async Task Per_line_staging_stages_only_the_chosen_lines()
    {
        await Write(FileA, "one", "TWO", "THREE", "four", "five", "six");
        await _git.RefreshAsync(Ct);
        var diff = (await _git.DiffAsync(FileA, staged: false, Ct))!;
        var hunk = Assert.Single(diff.Hunks);
        var twoAdded = hunk.Lines.Select((l, i) => (l, i)).Single(x => x.l is { Kind: '+', Text: "TWO" }).i;
        var twoRemoved = hunk.Lines.Select((l, i) => (l, i)).Single(x => x.l is { Kind: '-', Text: "two" }).i;

        await _git.StageLinesAsync(diff, hunk, new HashSet<int> { twoAdded, twoRemoved }, Ct);
        var staged = (await _git.DiffAsync(FileA, staged: true, Ct))!;
        var stagedLines = staged.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind != ' ').ToList();
        Assert.Equal([new DiffLine('-', "two"), new DiffLine('+', "TWO")], stagedLines);

        // The rest is still unstaged; unstaging the line brings the index back.
        var unstaged = (await _git.DiffAsync(FileA, staged: false, Ct))!;
        Assert.Equal([new DiffLine('-', "three"), new DiffLine('+', "THREE")], unstaged.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind != ' ').ToList());
        var stagedHunk = Assert.Single(staged.Hunks);
        var added = stagedHunk.Lines.Select((l, i) => (l, i)).Single(x => x.l.Kind == '+').i;
        var removed = stagedHunk.Lines.Select((l, i) => (l, i)).Single(x => x.l.Kind == '-').i;
        await _git.UnstageLinesAsync(staged, stagedHunk, new HashSet<int> { added, removed }, Ct);
        Assert.Null(await _git.DiffAsync(FileA, staged: true, Ct));
    }

    [Fact]
    public async Task Remotes_clone_init_ignore_templates_and_submodules()
    {
        // A bare remote: add, push a tag to it, rename, then clone it.
        var bare = Path.Combine(_root, "remote.git");
        await Git(_root, "init", "-q", "--bare", "-b", "main", bare);
        await _git.AddRemoteAsync("origin", bare, Ct);
        await _git.PushAsync(Ct);
        await _git.CreateTagAsync("v2", null, null, Ct);
        await _git.PushTagAsync("v2", "origin", Ct);
        Assert.Contains("refs/tags/v2", await Git(bare, "show-ref"), StringComparison.Ordinal);
        await _git.RenameRemoteAsync("origin", "upstream", Ct);
        Assert.Equal(("upstream", bare), ((await _git.RemotesAsync(Ct)).Single().Name, (await _git.RemotesAsync(Ct)).Single().FetchUrl));

        var clone = Path.Combine(_root, "clone");
        await _git.CloneAsync(bare, clone, Ct);
        Assert.True(File.Exists(Path.Combine(clone, "a.txt")));

        // Init writes a .NET .gitignore.
        var fresh = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(fresh);
        await _git.InitAsync(fresh, Ct);
        Assert.True(Directory.Exists(Path.Combine(fresh, ".git")));
        Assert.Contains("[Bb]in/", await File.ReadAllTextAsync(Path.Combine(fresh, ".gitignore"), Ct), StringComparison.Ordinal);

        // Add to .gitignore: once.
        var logs = Path.Combine(Repo, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "x.log"), "x", Ct);
        await _git.IgnoreAsync(logs, Ct);
        await _git.IgnoreAsync(logs, Ct);
        Assert.Equal("/logs/\n", await File.ReadAllTextAsync(Path.Combine(Repo, ".gitignore"), Ct));
        Assert.DoesNotContain(_git.Status!.Files, f => f.Path.Contains("x.log", StringComparison.Ordinal));

        // The commit template.
        Assert.Null(await _git.CommitTemplateAsync(Ct));
        await File.WriteAllTextAsync(Path.Combine(Repo, ".gitmessage"), "Summary\n\nWhy:\n", Ct);
        await Git(Repo, "config", "commit.template", ".gitmessage");
        Assert.Equal("Summary\n\nWhy:\n", await _git.CommitTemplateAsync(Ct));

        // Submodules: listed, and updated after a fresh clone.
        await Git(Repo, "-c", "protocol.file.allow=always", "submodule", "add", "-q", bare, "lib");
        await Commit("add a submodule");
        await _git.RefreshAsync(Ct);
        var submodule = Assert.Single(await _git.SubmodulesAsync(Ct));
        Assert.Equal(Path.Combine(Repo, "lib"), submodule.Path);
        Assert.True(submodule.IsInitialized);
    }
}
