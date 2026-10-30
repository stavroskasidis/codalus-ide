using Codalus.Projects.Host;
using Codalus.Projects.Protocol;

namespace Codalus.Projects.Tests;

/// <summary>
/// What the build host gives the Solution Explorer so that the shell's UI thread never waits on the disk (M56): Show All
/// Files' listings, down to the folders asked for, and which analyzers are missing.
/// </summary>
public sealed class SolutionExplorerHostTests : IDisposable
{
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

    [Fact]
    public void Analyzers_that_arent_on_disk_are_marked_for_the_solution_explorer()
    {
        var present = typeof(SolutionExplorerHostTests).Assembly.Location;
        var missing = P("packages", "Missing.Analyzers.dll");
        var inputs = new CompilerInputsInfo(P("App", "App.csproj"), "net10.0", "C#", CompilerInputStates.Ok, null,
            [$"/analyzer:{present}", $"/analyzer:\"{missing}\"", $"-analyzer:{missing}", "/reference:" + P("Lib.dll"), "A.cs"], [], null, null);

        Assert.Equal([missing], ExplorerFiles.WithMissingAnalyzers(inputs).MissingAnalyzers ?? []);

        // None missing, or a failed build: sent as they are.
        var complete = inputs with { Arguments = [$"/analyzer:{present}", "A.cs"] };
        Assert.Same(complete, ExplorerFiles.WithMissingAnalyzers(complete));
        Assert.Null(complete.MissingAnalyzers);
        var failed = inputs with { State = CompilerInputStates.Failed };
        Assert.Same(failed, ExplorerFiles.WithMissingAnalyzers(failed));
    }
}
