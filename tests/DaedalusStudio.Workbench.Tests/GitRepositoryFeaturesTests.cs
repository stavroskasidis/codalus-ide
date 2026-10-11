using System.Diagnostics;
using DaedalusStudio.Tests;
using DaedalusStudio.Workbench.Git;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>
/// M13b (ADR 0031) on scripted repositories in a temporary folder: worktrees, Git LFS pointer files and locks, solutions
/// spanning several repositories, fetching and switching to a branch or a pull request ref, publishing, and askpass.
/// </summary>
public sealed class GitRepositoryFeaturesTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(DaedalusStudio.Platform.RealPath.Resolve(Path.GetTempPath()), "daedalus-gitrepos-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly GitService _git = new(watch: false);

    private string Repo => Path.Combine(_root, "repo");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Repo);
        await InitAsync(Repo);
        await File.WriteAllTextAsync(Path.Combine(Repo, "a.txt"), "one\n", Ct);
        await Git(Repo, "add", "-A");
        await Git(Repo, "commit", "-q", "-m", "initial");
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

    private static async Task InitAsync(string folder, bool bare = false)
    {
        Directory.CreateDirectory(folder);
        if (bare)
        {
            await Git(folder, "init", "-q", "--bare", "-b", "main");
        }
        else
        {
            await Git(folder, "init", "-q", "-b", "main");
        }
        if (!bare)
        {
            await Git(folder, "config", "user.name", "Ada Tester");
            await Git(folder, "config", "user.email", "ada@example.com");
            await Git(folder, "config", "commit.gpgsign", "false");
            await Git(folder, "config", "core.autocrlf", "false");
            await Git(folder, "config", "credential.helper", string.Empty);
        }
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
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

    // ------------------------------------------------------------------ worktrees

    [Fact]
    public async Task Worktrees_are_listed_added_locked_removed_and_pruned()
    {
        var single = Assert.Single(await _git.WorktreesAsync(Ct));
        Assert.True(single.IsMain);
        Assert.Equal("main", single.Branch);

        var feature = Path.Combine(_root, "repo-feature");
        await _git.AddWorktreeAsync(feature, "feature", null, Ct);
        Assert.True(File.Exists(Path.Combine(feature, "a.txt")));
        var detached = Path.Combine(_root, "repo-detached");
        await _git.AddWorktreeAsync(detached, null, "HEAD", Ct);

        var all = await _git.WorktreesAsync(Ct);
        Assert.Equal(3, all.Count);
        var added = all.Single(w => w.Path == Path.GetFullPath(feature));
        Assert.Equal("feature", added.Branch);
        Assert.False(added.IsMain);
        Assert.True(all.Single(w => w.Path == Path.GetFullPath(detached)).IsDetached);

        await _git.LockWorktreeAsync(feature, "on a USB disk", Ct);
        var locked = (await _git.WorktreesAsync(Ct)).Single(w => w.Path == Path.GetFullPath(feature));
        Assert.True(locked.IsLocked);
        Assert.Equal("on a USB disk", locked.LockReason);
        await _git.UnlockWorktreeAsync(feature, Ct);

        // A worktree with changes needs force.
        await File.WriteAllTextAsync(Path.Combine(feature, "a.txt"), "changed\n", Ct);
        await Assert.ThrowsAsync<GitException>(() => _git.RemoveWorktreeAsync(feature, force: false, Ct));
        await _git.RemoveWorktreeAsync(feature, force: true, Ct);
        Assert.False(Directory.Exists(feature));

        // A worktree whose folder was deleted is prunable, then pruned.
        Directory.Delete(detached, recursive: true);
        Assert.True((await _git.WorktreesAsync(Ct)).Single(w => w.Path == Path.GetFullPath(detached)).IsPrunable);
        await _git.PruneWorktreesAsync(Ct);
        Assert.Single(await _git.WorktreesAsync(Ct));
    }

    [Fact]
    public void Worktree_porcelain_is_parsed()
    {
        var root = OperatingSystem.IsWindows() ? "C:/r" : "/r";
        var worktrees = GitParsers.ParseWorktrees($"""
            worktree {root}/main
            HEAD 1111111111111111111111111111111111111111
            branch refs/heads/main

            worktree {root}/wt1
            HEAD 2222222222222222222222222222222222222222
            detached
            locked

            worktree {root}/wt2
            HEAD 3333333333333333333333333333333333333333
            branch refs/heads/topic/x
            prunable gitdir file points to non-existent location

            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(3, worktrees.Count);
        Assert.True(worktrees[0].IsMain);
        Assert.True(worktrees[1] is { IsDetached: true, IsLocked: true, LockReason: null, IsMain: false });
        Assert.True(worktrees[2] is { Branch: "topic/x", IsPrunable: true });
        Assert.Equal("gitdir file points to non-existent location", worktrees[2].PrunableReason);
    }

    // ------------------------------------------------------------------ Git LFS

    [Fact]
    public async Task Lfs_pointer_files_and_locks_are_found()
    {
        Assert.False((await _git.LfsStatusAsync(cancellationToken: Ct)).IsUsed);

        await File.WriteAllTextAsync(Path.Combine(Repo, ".gitattributes"), "*.bin filter=lfs diff=lfs merge=lfs -text\n", Ct);
        Directory.CreateDirectory(Path.Combine(Repo, "assets"));
        var pointer = Path.Combine(Repo, "assets", "big.bin");
        await File.WriteAllTextAsync(pointer, "version https://git-lfs.github.com/spec/v1\noid sha256:4d7a214614ab2935c943f9e0ff69d22eadbb8f32b1258daaa5e2ca24d17e2393\nsize 12345\n", Ct);
        await Git(Repo, "add", "-A");
        await Git(Repo, "commit", "-q", "-m", "lfs");
        var real = Path.Combine(Repo, "assets", "real.bin");
        await File.WriteAllBytesAsync(real, [1, 2, 3, 4], Ct);

        await using var server = new MockHttpServer(request => request.Path.EndsWith("/locks", StringComparison.Ordinal)
            ? MockHttpResponse.Json("""{ "locks": [ { "id": "7", "path": "assets/big.bin", "locked_at": "2026-10-11T08:00:00Z", "owner": { "name": "Ada Tester" } } ] }""")
            : MockHttpResponse.NotFound());
        await Git(Repo, "config", "lfs.url", server.BaseUrl + "/repo.git/info/lfs");

        var status = await _git.LfsStatusAsync(cancellationToken: Ct);
        Assert.True(status.IsUsed);
        var big = status.FileOf(pointer);
        Assert.NotNull(big);
        Assert.True(big!.IsPointer);
        Assert.Equal(12345, big.Size);
        Assert.Equal("assets/big.bin", big.RelativePath);
        Assert.False(status.FileOf(real)!.IsPointer);
        Assert.Null(status.FileOf(Path.Combine(Repo, "a.txt")));

        if (!status.IsInstalled)
        {
            Assert.Empty(status.Locks);
            Assert.Equal("git-lfs is not installed.", status.LocksError);
            return;
        }

        Assert.True(status.LocksError is null, status.LocksError);
        var lfsLock = Assert.Single(status.Locks);
        Assert.Equal("Ada Tester", lfsLock.Owner);
        Assert.Equal("7", lfsLock.Id);
        Assert.Equal(pointer, status.LockOf(pointer)?.Path);
    }

    [Fact]
    public void Lfs_lock_json_is_parsed()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "r"));
        var locks = GitParsers.ParseLfsLocks(root, """[ { "id": "1", "path": "x/y.psd", "owner": { "name": "Bo" }, "locked_at": "2026-01-02T03:04:05Z" } ]""");
        Assert.Equal(Path.Combine(root, "x", "y.psd"), Assert.Single(locks).Path);
        Assert.Equal(2026, locks[0].LockedAt!.Value.Year);
        Assert.Empty(GitParsers.ParseLfsLocks(root, string.Empty));
    }

    // ------------------------------------------------------------------ several repositories

    [Fact]
    public async Task A_solution_spanning_several_repositories_finds_each_and_reads_files_from_their_own()
    {
        var other = Path.Combine(_root, "other");
        await InitAsync(other);
        Directory.CreateDirectory(Path.Combine(other, "src", "Lib"));
        var library = Path.Combine(other, "src", "Lib", "Lib.cs");
        await File.WriteAllTextAsync(library, "class Lib { }\n", Ct);
        await Git(other, "add", "-A");
        await Git(other, "commit", "-q", "-m", "lib");

        var nested = Path.Combine(Repo, "vendor", "tool");
        await InitAsync(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "tool.txt"), "tool\n", Ct);
        await Git(nested, "add", "-A");
        await Git(nested, "commit", "-q", "-m", "tool");

        Directory.CreateDirectory(Path.Combine(Repo, "src", "App"));
        var found = await _git.FindRepositoriesAsync([Repo, Path.Combine(Repo, "src", "App"), Path.Combine(other, "src", "Lib"), nested, Path.Combine(_root, "missing")], Ct);
        Assert.Equal([Path.GetFullPath(Repo), Path.GetFullPath(other), Path.GetFullPath(nested)], found);

        _git.Repositories = found;
        Assert.Equal(Path.GetFullPath(other), _git.RepositoryFor(library));
        Assert.Equal(Path.GetFullPath(nested), _git.RepositoryFor(Path.Combine(nested, "tool.txt")));
        Assert.Equal(Path.GetFullPath(Repo), _git.RepositoryFor(Path.Combine(Repo, "a.txt")));
        Assert.Null(_git.RepositoryFor(Path.Combine(_root, "elsewhere.txt")));

        Assert.Equal("class Lib { }\n", await _git.ShowInRepositoryAsync(library, "HEAD", Ct));
        Assert.Equal("class Lib { }\n", await _git.ShowInRepositoryAsync(library, ":", Ct));
        var blame = await _git.BlameInRepositoryAsync(library, "class Lib { }\n", Ct);
        Assert.Equal("lib", Assert.Single(blame).Summary);
        Assert.Equal("main", (await _git.PeekStatusAsync(other, Ct))?.Branch);
    }

    // ------------------------------------------------------------------ pull requests and publishing

    [Fact]
    public async Task A_branch_or_a_pull_request_ref_is_fetched_and_switched_to()
    {
        var bare = Path.Combine(_root, "origin.git");
        await InitAsync(bare, bare: true);
        await _git.AddRemoteAsync("origin", bare, Ct);
        await _git.PublishBranchAsync("origin", "main", Ct);
        Assert.Equal("origin/main", _git.Status?.Upstream);
        Assert.Equal("github", await _git.FreeRemoteNameAsync("origin", "github", Ct));
        Assert.Equal("upstream", await _git.FreeRemoteNameAsync("upstream", "github", Ct));

        // Someone else pushes a feature branch and a pull request ref.
        var clone = Path.Combine(_root, "clone");
        await Git(_root, "clone", "-q", bare, clone);
        await Git(clone, "config", "user.name", "Bo Reviewer");
        await Git(clone, "config", "user.email", "bo@example.com");
        await Git(clone, "config", "commit.gpgsign", "false");
        await Git(clone, "switch", "-q", "-c", "feature");
        await File.WriteAllTextAsync(Path.Combine(clone, "b.txt"), "feature\n", Ct);
        await Git(clone, "add", "-A");
        await Git(clone, "commit", "-q", "-m", "feature work");
        await Git(clone, "push", "-q", "origin", "feature", "HEAD:refs/pull/7/head");

        await _git.FetchAndSwitchAsync("origin", "feature", "feature", track: true, Ct);
        Assert.Equal("feature", _git.Status?.Branch);
        Assert.Equal("origin/feature", _git.Status?.Upstream);
        Assert.True(File.Exists(Path.Combine(Repo, "b.txt")));

        await _git.FetchAndSwitchAsync("origin", "refs/pull/7/head", "pr/7", track: false, Ct);
        Assert.Equal("pr/7", _git.Status?.Branch);
        Assert.Null(_git.Status?.Upstream);

        // The pull request gets another commit: switching again fast-forwards.
        await File.WriteAllTextAsync(Path.Combine(clone, "b.txt"), "feature, reviewed\n", Ct);
        await Git(clone, "commit", "-q", "-am", "review fixes");
        await Git(clone, "push", "-q", "-f", "origin", "HEAD:refs/pull/7/head");
        await _git.SwitchAsync(new GitBranch("main", false, false, null, string.Empty), Ct);
        await _git.FetchAndSwitchAsync("origin", "refs/pull/7/head", "pr/7", track: false, Ct);
        Assert.Equal("pr/7", _git.Status?.Branch);
        Assert.Equal("feature, reviewed\n", await File.ReadAllTextAsync(Path.Combine(Repo, "b.txt"), Ct));
        Assert.True(await _git.IsBranchPublishedAsync("origin", "feature", Ct));
        Assert.False(await _git.IsBranchPublishedAsync("origin", "pr/7", Ct));
        Assert.Equal(_git.Status?.Head, await _git.ResolveRefAsync("pr/7", Ct));
    }

    // ------------------------------------------------------------------ askpass

    [Fact]
    public async Task Askpass_answers_through_the_pipe_and_never_without_the_token()
    {
        var asked = new List<AskpassPrompt>();
        using var server = new AskpassServer("daedalus", (prompt, _) =>
        {
            lock (asked)
            {
                asked.Add(prompt);
            }

            return Task.FromResult(prompt.IsSecret ? "s3cret" : prompt.Text.StartsWith("Username", StringComparison.Ordinal) ? "ada" : null);
        });
        string? Environment(string name) => server.Environment.TryGetValue(name, out var value) ? value : null;
        Assert.Equal("daedalus", server.Environment["GIT_ASKPASS"]);
        Assert.Equal("force", server.Environment["SSH_ASKPASS_REQUIRE"]);
        Assert.True(AskpassClient.IsInvocation(["Password: "], Environment));
        Assert.False(AskpassClient.IsInvocation(["a", "b"], Environment));
        Assert.False(AskpassClient.IsInvocation(["Password: "], _ => null));

        string Run(string prompt, Func<string, string?> environment, out int code)
        {
            var output = new StringWriter();
            code = AskpassClient.Run(prompt, output, environment);
            return output.ToString();
        }

        var answer = await Task.Run(() => (Run("Password for 'https://ada@example.com': ", Environment, out var code), code), Ct);
        Assert.Equal(("s3cret\n", 0), answer);
        answer = await Task.Run(() => (Run("Username for 'https://example.com': ", Environment, out var code), code), Ct);
        Assert.Equal(("ada\n", 0), answer);

        // Cancelled by the user, a wrong token, no shell: exit code 1 and nothing printed.
        answer = await Task.Run(() => (Run("Are you sure you want to continue connecting (yes/no/[fingerprint])? ", Environment, out var code), code), Ct);
        Assert.Equal((string.Empty, 1), answer);
        answer = await Task.Run(() => (Run("Password: ", name => name == AskpassClient.TokenVariable ? "wrong" : Environment(name), out var code), code), Ct);
        Assert.Equal((string.Empty, 1), answer);
        answer = await Task.Run(() => (Run("Password: ", name => name == AskpassClient.PipeVariable ? "daedalus-ask-none" : Environment(name), out var code), code), Ct);
        Assert.Equal((string.Empty, 1), answer);
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public void Askpass_prompts_are_classified()
    {
        Assert.True(new AskpassPrompt("Password for 'https://ada@github.com': ").IsSecret);
        Assert.True(new AskpassPrompt("Enter passphrase for key '/home/ada/.ssh/id_ed25519': ").IsSecret);
        Assert.Equal("SSH Key Passphrase", new AskpassPrompt("Enter passphrase for key 'k': ").Title);
        Assert.False(new AskpassPrompt("Username for 'https://github.com': ").IsSecret);
        Assert.Equal("Git: Sign In", new AskpassPrompt("Username for 'https://github.com': ").Title);
        var hostKey = new AskpassPrompt("The authenticity of host 'h' can't be established.\nAre you sure you want to continue connecting (yes/no/[fingerprint])? ");
        Assert.True(hostKey.IsConfirmation);
        Assert.False(hostKey.IsSecret);
    }
}
