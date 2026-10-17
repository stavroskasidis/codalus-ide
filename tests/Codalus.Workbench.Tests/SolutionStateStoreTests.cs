using System.Text.Json.Serialization;
using Codalus.Workbench.Bookmarks;
using Codalus.Workbench.Projects;

namespace Codalus.Workbench.Tests;

/// <summary>Per-solution state next to the solution (ADR 0070): layout, Git, migration, fallback and bookmarks.</summary>
public sealed class SolutionStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-state-" + Guid.NewGuid().ToString("N"));

    private string UserState => Path.Combine(_root, "user", "state");

    private string Repo => Path.Combine(_root, "repo");

    public SolutionStateStoreTests() => Directory.CreateDirectory(Repo);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Dictionary<string, string> Value(string text) => new() { ["value"] = text };

    private static string? Text(Dictionary<string, string>? value) => value?.GetValueOrDefault("value");

    [Fact]
    public async Task State_is_kept_per_solution_file_in_a_self_ignoring_codalus_state_folder()
    {
        var store = new SolutionStateStore(UserState);
        var app = new SolutionStateKey(Path.Combine(Repo, "App.slnx"), IsFolder: false);
        var all = new SolutionStateKey(Path.Combine(Repo, "All.sln"), IsFolder: false);
        var folder = new SolutionStateKey(Repo, IsFolder: true);

        await store.WriteAsync(app, SolutionStateStore.SessionFile, Value("app"), TestJsonContext.Default.DictionaryStringString);
        await store.WriteAsync(all, SolutionStateStore.SessionFile, Value("all"), TestJsonContext.Default.DictionaryStringString);
        await store.WriteAsync(folder, SolutionStateStore.SessionFile, Value("folder"), TestJsonContext.Default.DictionaryStringString);
        await store.FlushAsync();

        Assert.True(File.Exists(Path.Combine(Repo, ".codalus", "state", "App.slnx", "session.json")));
        Assert.True(File.Exists(Path.Combine(Repo, ".codalus", "state", "All.sln", "session.json")));
        Assert.True(File.Exists(Path.Combine(Repo, ".codalus", "state", "folder", "session.json")));
        Assert.Contains("*", File.ReadAllLines(Path.Combine(Repo, ".codalus", "state", ".gitignore")));
        Assert.Empty(Directory.EnumerateFiles(Repo, "*.tmp", SearchOption.AllDirectories)); // written through a temporary file and a rename
        Assert.False(Directory.Exists(Path.Combine(UserState, "solution-state")));

        Assert.Equal("app", Text(await store.ReadAsync(app, SolutionStateStore.SessionFile, TestJsonContext.Default.DictionaryStringString)));
        Assert.Equal("all", Text(await store.ReadAsync(all, SolutionStateStore.SessionFile, TestJsonContext.Default.DictionaryStringString)));
        Assert.Equal("folder", Text(await store.ReadAsync(folder, SolutionStateStore.SessionFile, TestJsonContext.Default.DictionaryStringString)));
        Assert.Null(await store.ReadAsync(app, SolutionStateStore.BookmarksFile, TestJsonContext.Default.DictionaryStringString));

        // Git ignores it even without a .gitignore of the repository's own.
        await File.WriteAllTextAsync(Path.Combine(Repo, "App.slnx"), "<Solution />", TestContext.Current.CancellationToken);
        await Git(Repo, "init", "-q");
        var status = await Git(Repo, "status", "--porcelain", "--untracked-files=all");
        Assert.Contains("App.slnx", status, StringComparison.Ordinal);
        Assert.DoesNotContain(".codalus", status, StringComparison.Ordinal);
    }

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }

    [Fact]
    public async Task The_old_session_file_moves_next_to_the_solution_and_is_deleted()
    {
        var store = new SolutionStateStore(UserState);
        var key = new SolutionStateKey(Path.Combine(Repo, "App.slnx"), IsFolder: false);
        var legacy = store.LegacySessionFile(key.Path);
        Assert.Equal(Path.Combine(UserState, "sessions", SolutionStateStore.Hash(key.Path) + ".json"), legacy);
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        await File.WriteAllTextAsync(legacy, """{ "value": "old" }""", TestContext.Current.CancellationToken);

        Assert.Equal("old", Text(await store.ReadAsync(key, SolutionStateStore.SessionFile, TestJsonContext.Default.DictionaryStringString, legacy)));
        Assert.False(File.Exists(legacy));
        Assert.Contains("old", File.ReadAllText(Path.Combine(Repo, ".codalus", "state", "App.slnx", "session.json")), StringComparison.Ordinal);

        // The moved file is what's read from then on.
        Assert.Equal("old", Text(await store.ReadAsync(key, SolutionStateStore.SessionFile, TestJsonContext.Default.DictionaryStringString, legacy)));
    }

    [Fact]
    public async Task A_folder_that_cant_be_written_falls_back_to_the_user_state_folder_until_it_can()
    {
        var logged = new List<string>();
        var store = new SolutionStateStore(UserState, line =>
        {
            lock (logged)
            {
                logged.Add(line);
            }
        });
        var key = new SolutionStateKey(Path.Combine(Repo, "App.slnx"), IsFolder: false);

        // A file where the folder would go: creating .codalus/state fails, for root too (unlike permissions).
        var blocker = Path.Combine(Repo, ".codalus");
        await File.WriteAllTextAsync(blocker, string.Empty, TestContext.Current.CancellationToken);
        await store.WriteAsync(key, SolutionStateStore.SessionFile, Value("kept"), TestJsonContext.Default.DictionaryStringString);
        var fallback = Path.Combine(store.FallbackDirectory(key), SolutionStateStore.SessionFile);
        Assert.True(File.Exists(fallback));
        Assert.Contains(logged, l => l.Contains("Could not write session.json", StringComparison.Ordinal));
        Assert.Equal("kept", Text(await store.ReadAsync(key, SolutionStateStore.SessionFile, TestJsonContext.Default.DictionaryStringString)));

        // Writable again: the next write goes next to the solution, and the fallback copy is deleted.
        File.Delete(blocker);
        await store.WriteAsync(key, SolutionStateStore.SessionFile, Value("back"), TestJsonContext.Default.DictionaryStringString);
        Assert.False(File.Exists(fallback));
        Assert.True(File.Exists(Path.Combine(Repo, ".codalus", "state", "App.slnx", "session.json")));
        Assert.Equal("back", Text(await store.ReadAsync(key, SolutionStateStore.SessionFile, TestJsonContext.Default.DictionaryStringString)));
    }

    [Fact]
    public async Task Bookmarks_are_kept_per_solution_and_the_user_files_bookmarks_in_its_folder_move_to_it()
    {
        var userFile = Path.Combine(UserState, "bookmarks.json");
        var inside = Path.Combine(Repo, "src", "A.cs");
        var outside = Path.Combine(_root, "elsewhere", "B.cs");
        var service = new BookmarkService(userFile);
        service.Toggle(inside, 3);
        service.Toggle(outside, 7);

        var store = new SolutionStateStore(UserState);
        var app = new SolutionStateKey(Path.Combine(Repo, "App.slnx"), IsFolder: false);
        var changes = 0;
        service.Changed += (_, _) => changes++;
        await service.OpenSolutionAsync(store, app);
        Assert.Equal(1, changes);
        Assert.Equal([(inside, 3)], service.All.Select(b => (b.Path, b.Line)));
        service.Toggle(inside, 9);
        await store.FlushAsync();
        var saved = File.ReadAllText(Path.Combine(Repo, ".codalus", "state", "App.slnx", "bookmarks.json"));
        Assert.Contains("A.cs", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("B.cs", saved, StringComparison.Ordinal);

        // Closed: the user file's bookmarks (those outside the solution) again; they no longer include the moved ones.
        service.CloseSolution();
        Assert.Equal([(outside, 7)], service.All.Select(b => (b.Path, b.Line)));
        Assert.DoesNotContain("A.cs", File.ReadAllText(userFile), StringComparison.Ordinal);

        // A second solution in the folder starts empty (the first took the folder's bookmarks); the first keeps its own.
        await service.OpenSolutionAsync(store, new SolutionStateKey(Path.Combine(Repo, "Other.slnx"), IsFolder: false));
        Assert.Empty(service.All);
        service.CloseSolution();
        await service.OpenSolutionAsync(store, app);
        Assert.Equal([3, 9], service.All.Select(b => b.Line));
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class TestJsonContext : JsonSerializerContext;
