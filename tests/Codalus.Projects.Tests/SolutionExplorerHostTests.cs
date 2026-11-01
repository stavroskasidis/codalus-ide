using Codalus.Projects.Host;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Tests;

/// <summary>
/// What the build host gives the Solution Explorer so that the shell's UI thread never waits on the disk (M56): Show All
/// Files' listings, down to the folders asked for, and which analyzers are missing.
/// </summary>
public sealed class SolutionExplorerHostTests : IDisposable
{
    private static readonly string[] s_skipped = ["node_modules", ".vs", ".idea"];

    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-explorer-host-" + Guid.NewGuid().ToString("N"));

    public SolutionExplorerHostTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string P(params string[] segments) => Path.Combine([_root, .. segments]);

    [Fact]
    public void A_listing_goes_only_into_the_folders_asked_for()
    {
        foreach (var file in new[] { P("App", "App.csproj"), P("App", "A.cs"), P("App", "Models", "M.cs"), P("App", "bin", "Debug", "App.dll"), P("App", "docs", "Notes.md") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, string.Empty);
        }

        var everything = ExplorerFiles.ListTree(new ListTreeParams(P("App")));
        Assert.Contains(new DirectoryEntry(P("App", "bin", "Debug", "App.dll"), false), everything.Entries);

        // The project's folders and an expanded one (with a separator at its end, as a path may come): bin is listed, not
        // what is in it.
        var listed = ExplorerFiles.ListTree(new ListTreeParams(P("App"), Folders: [P("App", "Models"), P("App", "docs") + Path.DirectorySeparatorChar]));
        Assert.Equal(
            new[] { P("App", "A.cs"), P("App", "App.csproj"), P("App", "bin"), P("App", "docs"), P("App", "docs", "Notes.md"), P("App", "Models"), P("App", "Models", "M.cs") }.Order(StringComparer.Ordinal),
            listed.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.True(listed.Entries.Single(e => e.Path == P("App", "bin")).IsDirectory);
        Assert.False(listed.Truncated);
    }

    private void Files(params string[][] files)
    {
        foreach (var segments in files)
        {
            var file = P(segments);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, string.Empty);
        }
    }

    [Fact]
    public void The_folder_view_lists_one_folder_at_a_time()
    {
        // Each expanded folder is listed alone, with no folders to go into (M57): what is directly in it.
        Files(["ws", "README.md"], ["ws", "src", "App", "App.csproj"], ["ws", "src", "App", "Models", "M.cs"], ["ws", "docs", "a.md"]);

        var top = ExplorerFiles.ListTree(new ListTreeParams(P("ws"), Folders: []));
        Assert.Equal(new[] { P("ws", "docs"), P("ws", "README.md"), P("ws", "src") }.Order(StringComparer.Ordinal), top.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.False(top.Truncated);

        var app = ExplorerFiles.ListTree(new ListTreeParams(P("ws", "src", "App"), Folders: []));
        Assert.Equal(new[] { P("ws", "src", "App", "App.csproj"), P("ws", "src", "App", "Models") }.Order(StringComparer.Ordinal), app.Entries.Select(e => e.Path).Order(StringComparer.Ordinal));
        Assert.Empty(ExplorerFiles.ListTree(new ListTreeParams(P("ws", "gone"), Folders: [])).Entries);
    }

    [Fact]
    public void A_listing_cut_at_its_limit_says_so()
    {
        Files([.. Enumerable.Range(0, 12).Select(i => new[] { "big", $"F{i:00}.txt" })]);

        var cut = ExplorerFiles.ListTree(new ListTreeParams(P("big"), MaxEntries: 10, Folders: []));
        Assert.True(cut.Truncated);
        Assert.Equal(10, cut.Entries.Length);

        var whole = ExplorerFiles.ListTree(new ListTreeParams(P("big"), MaxEntries: 12, Folders: []));
        Assert.False(whole.Truncated);
        Assert.Equal(12, whole.Entries.Length);
    }

    [Fact]
    public void Ide_and_package_folders_are_listed_marked_and_gone_into_only_when_asked_and_git_never_is()
    {
        // node_modules, .vs and .idea were left out, so neither Show All Files nor the folder view showed them (M57). They're
        // listed now, marked, and gone into only when named; .git stays out, and the project discovery still skips them all.
        Files(["ws", "App", "App.csproj"], ["ws", "node_modules", "pkg", "pkg.csproj"], ["ws", ".vs", "ws", "v18", ".suo"], ["ws", ".idea", "workspace.xml"],
            ["ws", ".git", "config"], ["ws", ".idea", "Hidden.csproj"], ["ws", "App", "bin", "Debug", "Out.csproj"]);

        var everything = ExplorerFiles.ListTree(new ListTreeParams(P("ws")));
        var paths = everything.Entries.Select(e => e.Path).ToList();
        Assert.All(s_skipped, name => Assert.Contains(new DirectoryEntry(P("ws", name), true) { Skipped = true }, everything.Entries));
        Assert.DoesNotContain(paths, p => p.StartsWith(P("ws", "node_modules") + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                           || p.StartsWith(P("ws", ".vs") + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                           || p.StartsWith(P("ws", ".idea") + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.DoesNotContain(paths, p => p.Contains(".git", StringComparison.Ordinal));
        Assert.Contains(new DirectoryEntry(P("ws", "App"), true), everything.Entries); // not marked

        // Named, node_modules is gone into: what is in it isn't marked. Its own listing (the folder view's) is the same.
        var expanded = ExplorerFiles.ListTree(new ListTreeParams(P("ws"), Folders: [P("ws", "node_modules")]));
        Assert.Contains(new DirectoryEntry(P("ws", "node_modules", "pkg"), true), expanded.Entries);
        Assert.Equal([new DirectoryEntry(P("ws", "node_modules", "pkg"), true)], ExplorerFiles.ListTree(new ListTreeParams(P("ws", "node_modules"), Folders: [])).Entries);
        Assert.DoesNotContain(ExplorerFiles.ListTree(new ListTreeParams(P("ws"), Folders: [])).Entries, e => e.Path.EndsWith(".git", StringComparison.Ordinal));

        Assert.Equal([P("ws", "App", "App.csproj")], ExplorerFiles.DiscoverProjects(P("ws"), [".csproj"]));

        // On the wire, the mark is left out when false.
        Assert.Equal("""{"entries":[{"path":"/a","isDirectory":true},{"path":"/b","isDirectory":true,"skipped":true}],"truncated":false}""",
            System.Text.Json.JsonSerializer.Serialize(new ListTreeResult([new("/a", true), new("/b", true) { Skipped = true }], false), ItemJsonContext.Default.ListTreeResult));
    }

    [Fact]
    public void Analyzers_that_arent_on_disk_are_marked_for_the_solution_explorer()
    {
        var present = typeof(SolutionExplorerHostTests).Assembly.Location;
        var missing = P("packages", "Missing.Analyzers.dll");
        var inputs = new CompilerInputsInfo(P("App", "App.csproj"), "net10.0", "C#", CompilerInputStates.Ok, null,
            [$"/analyzer:{present}", $"/analyzer:\"{missing}\"", $"-analyzer:{missing}", "/reference:" + P("Lib.dll"), "A.cs"], [], null, null);

        Assert.Equal([missing], ExplorerFiles.WithMissingAnalyzers(inputs).MissingAnalyzers ?? []);

        // An Analyzer item's relative path is the project folder's, not the host's working folder's.
        Directory.CreateDirectory(P("Gen"));
        File.WriteAllText(P("Gen", "Gen.dll"), string.Empty);
        var relative = Path.Combine("..", "Gen", "Gen.dll");
        Assert.Equal([Path.Combine("..", "Gen", "Other.dll")], ExplorerFiles.WithMissingAnalyzers(inputs with { Arguments = [$"/analyzer:{relative}", $"/analyzer:{Path.Combine("..", "Gen", "Other.dll")}"] }).MissingAnalyzers ?? []);

        // None missing, or a failed build: sent as they are.
        var complete = inputs with { Arguments = [$"/analyzer:{present}", "A.cs"] };
        Assert.Same(complete, ExplorerFiles.WithMissingAnalyzers(complete));
        Assert.Null(complete.MissingAnalyzers);
        var failed = inputs with { State = CompilerInputStates.Failed };
        Assert.Same(failed, ExplorerFiles.WithMissingAnalyzers(failed));
    }

    [Fact]
    public void A_rescan_is_in_the_message_only_when_the_watcher_dropped_events()
    {
        // Show All Files lists every project again after an overflow (M56); a host from before reads as no rescan.
        var json = ItemJsonContext.Default.FilesChangedParams;
        Assert.Equal("""{"directories":["/a"]}""", System.Text.Json.JsonSerializer.Serialize(new FilesChangedParams(["/a"]), json));
        Assert.Equal("""{"directories":["/a"],"rescan":true}""", System.Text.Json.JsonSerializer.Serialize(new FilesChangedParams(["/a"]) { Rescan = true }, json));
        Assert.False(System.Text.Json.JsonSerializer.Deserialize("""{"directories":["/a"]}""", json)!.Rescan);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize("""{"directories":["/a"],"rescan":true}""", json)!.Rescan);
    }
}
