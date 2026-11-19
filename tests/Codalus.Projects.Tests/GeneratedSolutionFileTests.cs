using Codalus.Projects.Host;

namespace Codalus.Projects.Tests;

/// <summary>
/// A build writes its entry point (<c>build.slnf</c>) while the restore from opening the solution may still read it:
/// on Windows the build failed with a sharing violation (<c>ComposeProjectTests</c>, a loaded local run, docs/ci-flakes.md).
/// </summary>
public sealed class GeneratedSolutionFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "codalus-generated-" + Guid.NewGuid().ToString("N"));

    private string Filter => Path.Combine(_root, ".codalus", "state", "App.slnx", "build.slnf");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A reader as MSBuild and <c>dotnet restore</c> open a solution filter: sharing reads only.</summary>
    private FileStream OpenAsRestoreDoes() => new(Filter, FileMode.Open, FileAccess.Read, FileShare.Read);

    [Fact]
    public void The_same_text_is_not_written_again_while_a_restore_reads_it()
    {
        GeneratedSolutionFile.Write(Filter, "{ \"solution\": 1 }\n");
        var written = File.GetLastWriteTimeUtc(Filter);
        using (OpenAsRestoreDoes())
        {
            GeneratedSolutionFile.Write(Filter, "{ \"solution\": 1 }\n");
        }

        Assert.Equal("{ \"solution\": 1 }\n", File.ReadAllText(Filter));
        Assert.Equal(written, File.GetLastWriteTimeUtc(Filter));
    }

    [Fact]
    public async Task A_changed_text_is_written_once_the_restore_lets_go_of_the_file()
    {
        GeneratedSolutionFile.Write(Filter, "{ \"solution\": 1 }\n");
        var reader = OpenAsRestoreDoes();
        var released = Task.Run(async () =>
        {
            await Task.Delay(300);
            await reader.DisposeAsync();
        }, TestContext.Current.CancellationToken);
        GeneratedSolutionFile.Write(Filter, "{ \"solution\": 2 }\n");
        Assert.Equal("{ \"solution\": 2 }\n", File.ReadAllText(Filter));
        await released;
    }
}
