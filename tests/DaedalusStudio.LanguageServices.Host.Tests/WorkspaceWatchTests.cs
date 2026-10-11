using DaedalusStudio.LanguageServices.Host;

namespace DaedalusStudio.LanguageServices.Host.Tests;

/// <summary>
/// The language host watches a solution's projects with one watcher (M33): each watcher is an inotify instance on Linux,
/// and the default limit is 128 for all of the user's processes.
/// </summary>
public sealed class WorkspaceWatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daedalus-watch-" + Guid.NewGuid().ToString("N"));

    public WorkspaceWatchTests()
    {
        foreach (var folder in new[] { "repo/src/App", "repo/src/Lib", "repo/tests/Lib.Tests", "repo/samples/Matrix/Core21/Core21.App", "repo/samples/Matrix/Net80/Net80.App", "other/Tool" })
        {
            Directory.CreateDirectory(Path.Combine(_root, folder));
        }

        File.WriteAllText(Path.Combine(_root, "repo", "Repo.slnx"), "<Solution />");
        File.WriteAllText(Path.Combine(_root, "repo", "samples", "Matrix", "Matrix.sln"), string.Empty);
    }

    private string P(string relative) => Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Projects_of_a_solution_share_its_folders_watcher()
    {
        // Common ancestor src/, whose parent has the solution file.
        Assert.Equal(P("repo"), WorkspaceManager.SolutionFolderAbove(P("repo/src/App"), P("repo/src/Lib")));
        Assert.Equal(P("repo"), WorkspaceManager.SolutionFolderAbove(P("repo/src/App"), P("repo/tests/Lib.Tests")));

        // The nearest solution folder wins.
        Assert.Equal(P("repo/samples/Matrix"), WorkspaceManager.SolutionFolderAbove(P("repo/samples/Matrix/Core21/Core21.App"), P("repo/samples/Matrix/Net80/Net80.App")));
    }

    [Fact]
    public void Folders_without_a_solution_above_them_keep_their_own_watchers()
    {
        // Their common ancestor (the temp folder's child) and the folders above it have no solution file.
        Assert.Null(WorkspaceManager.SolutionFolderAbove(P("repo/src/App"), P("other/Tool")));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
