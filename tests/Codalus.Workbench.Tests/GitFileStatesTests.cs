using System.Diagnostics;
using Codalus.Workbench.Git;

namespace Codalus.Workbench.Tests;

/// <summary>Solution Explorer's source control states (#357) against real temporary repositories.</summary>
public sealed class GitFileStatesTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-gitstates-" + Guid.NewGuid().ToString("N"));
    private readonly GitService _git = new(watch: false);
    private GitFileStates _states = null!;

    private string Repo => Path.Combine(_root, "repo");

    private string Committed => Path.Combine(Repo, "src", "Committed.cs");

    private string App => Path.Combine(Repo, "src", "App.csproj");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Repo, "src"));
        await Git(Repo, "init", "-q", "-b", "main");
        await Git(Repo, "config", "user.name", "Ada Tester");
        await Git(Repo, "config", "user.email", "ada@example.com");
        await Git(Repo, "config", "commit.gpgsign", "false");
        await Git(Repo, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Committed, "class Committed { }\n", Ct);
        await File.WriteAllTextAsync(App, "<Project Sdk=\"Microsoft.NET.Sdk\" />\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(Repo, ".gitignore"), "bin/\n", Ct);
        await Git(Repo, "add", ".");
        await Git(Repo, "commit", "-q", "-m", "initial");
        Assert.True(await _git.OpenAsync(Repo, Ct));
        _states = new GitFileStates(_git) { Enabled = true };
    }

    public ValueTask DisposeAsync()
    {
        _states.Dispose();
        _git.Dispose();
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal); // git objects are read-only
        }

        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    private async Task RefreshAsync()
    {
        await _git.RefreshAsync(Ct);
        await _states.UpdateAsync(Ct);
    }

    /// <summary>
    /// A committed file has the lock, a changed one (the project file too) the check mark, a new one the plus whether or
    /// not it is staged; an ignored file and a file outside the repository have none.
    /// </summary>
    [Fact]
    public async Task Each_file_has_its_state_in_git()
    {
        var added = Path.Combine(Repo, "src", "Added.cs");
        var untracked = Path.Combine(Repo, "src", "Untracked.cs");
        var ignored = Path.Combine(Repo, "bin", "App.dll");
        var outside = Path.Combine(_root, "Outside.cs");
        await File.AppendAllTextAsync(App, "<!-- edited -->\n", Ct);
        await File.WriteAllTextAsync(added, "class Added { }\n", Ct);
        await Git(Repo, "add", "src/Added.cs");
        await File.WriteAllTextAsync(untracked, "class Untracked { }\n", Ct);
        Directory.CreateDirectory(Path.GetDirectoryName(ignored)!);
        await File.WriteAllTextAsync(ignored, "binary", Ct);
        await File.WriteAllTextAsync(outside, "class Outside { }\n", Ct);
        await RefreshAsync();

        Assert.Equal(GitFileState.Unchanged, _states.StateOf(Committed));
        Assert.Equal(GitFileState.Modified, _states.StateOf(App));
        Assert.Equal(GitFileState.Added, _states.StateOf(added));
        Assert.Equal(GitFileState.Added, _states.StateOf(untracked));
        Assert.Equal(GitFileState.None, _states.StateOf(ignored));
        Assert.Equal(GitFileState.None, _states.StateOf(outside));
        Assert.Equal(GitFileState.None, _states.StateOf(Path.Combine(Repo, "src")));
    }

    /// <summary>A commit takes the new file into the index's list: its plus becomes the lock, and <see cref="GitFileStates.Changed"/> says so.</summary>
    [Fact]
    public async Task A_committed_new_file_gets_the_lock_and_the_change_is_raised()
    {
        var added = Path.Combine(Repo, "src", "Added.cs");
        await File.WriteAllTextAsync(added, "class Added { }\n", Ct);
        await RefreshAsync();
        Assert.Equal(GitFileState.Added, _states.StateOf(added));

        var changes = 0;
        _states.Changed += (_, _) => Interlocked.Increment(ref changes);
        await Git(Repo, "add", "src/Added.cs");
        await Git(Repo, "commit", "-q", "-m", "add");
        await RefreshAsync();

        Assert.Equal(GitFileState.Unchanged, _states.StateOf(added));
        Assert.True(Volatile.Read(ref changes) > 0, "Changed was not raised");
    }

    /// <summary>A file a merge stopped on has the conflict's mark until it is resolved.</summary>
    [Fact]
    public async Task A_conflicted_file_has_the_conflict_mark()
    {
        await Git(Repo, "checkout", "-q", "-b", "other");
        await File.WriteAllTextAsync(Committed, "class Committed { int Other; }\n", Ct);
        await Git(Repo, "commit", "-q", "-am", "other");
        await Git(Repo, "checkout", "-q", "main");
        await File.WriteAllTextAsync(Committed, "class Committed { int Main; }\n", Ct);
        await Git(Repo, "commit", "-q", "-am", "main");
        await GitMayFail(Repo, "merge", "-q", "other");
        await RefreshAsync();

        Assert.Equal(GitFileState.Conflict, _states.StateOf(Committed));
        Assert.Equal(GitFileState.Unchanged, _states.StateOf(App));
    }

    /// <summary>With the glyphs' setting off nothing is read, and every file has no state.</summary>
    [Fact]
    public async Task Off_every_file_has_no_state()
    {
        await RefreshAsync();
        Assert.Equal(GitFileState.Unchanged, _states.StateOf(Committed));

        _states.Enabled = false;
        await RefreshAsync();
        Assert.Equal(GitFileState.None, _states.StateOf(Committed));

        _states.Enabled = true;
        await _states.UpdateAsync(Ct);
        Assert.Equal(GitFileState.Unchanged, _states.StateOf(Committed));
    }

    /// <summary>
    /// A solution opened through a link to the repository's folder (macOS's temporary folder is one: /var is /private/var)
    /// has its states, though git names the files by the resolved path.
    /// </summary>
    [Fact]
    public async Task A_path_through_a_link_has_the_state_of_the_file_it_links_to()
    {
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, Repo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip("Symbolic links can't be created here: " + ex.Message);
        }

        await File.AppendAllTextAsync(App, "<!-- edited -->\n", Ct);
        await RefreshAsync();

        Assert.Equal(GitFileState.Unchanged, _states.StateOf(Path.Combine(link, "src", "Committed.cs")));
        Assert.Equal(GitFileState.Modified, _states.StateOf(Path.Combine(link, "src", "App.csproj")));
        Directory.Delete(link);
    }

    private static Task<string> Git(string directory, params string[] arguments) => RunGit(directory, check: true, arguments);

    private static Task<string> GitMayFail(string directory, params string[] arguments) => RunGit(directory, check: false, arguments);

    private static async Task<string> RunGit(string directory, bool check, string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(!check || process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return await output;
    }
}
