using Codalus.Projects.Host;

namespace Codalus.Projects.Tests;

/// <summary>
/// The build host's file watcher keeps NuGet's restore outputs back while the IDE's own restore runs: a restore that
/// wrote them project by project (minutes of it with a package source unreachable off a VPN) evaluated and built each
/// one again as it came, restarting the C# analysis every time.
/// </summary>
public sealed class ProjectWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-watcher-" + Guid.NewGuid().ToString("N"));
    private readonly List<ProjectWatcher.Changes> _reported = [];
    private readonly ProjectWatcher _watcher;
    private readonly string _project;

    public ProjectWatcherTests()
    {
        _project = Path.Combine(_root, "App", "App.csproj");
        Directory.CreateDirectory(Path.Combine(_root, "App", "obj"));
        File.WriteAllText(_project, "<Project />");
        _watcher = new ProjectWatcher(changes =>
        {
            lock (_reported)
            {
                _reported.Add(changes);
            }

            return Task.CompletedTask;
        });
        _watcher.Watch(_root, null, [_project]);
    }

    private string Obj(string name) => Path.Combine(_root, "App", "obj", name);

    private void WriteRestoreOutputs()
    {
        File.WriteAllText(Obj("project.assets.json"), "{}");
        File.WriteAllText(Obj("App.csproj.nuget.g.props"), "<Project />");
        File.WriteAllText(Obj("App.csproj.nuget.g.targets"), "<Project />");
    }

    private List<ProjectWatcher.Changes> Reported()
    {
        lock (_reported)
        {
            return [.. _reported];
        }
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), "Timed out.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Restore_outputs_wait_for_the_restore_and_go_to_it()
    {
        _watcher.HoldRestoreOutputs();
        WriteRestoreOutputs();
        await Task.Delay(1500, TestContext.Current.CancellationToken); // well past the watcher's 300 ms debounce
        Assert.Empty(Reported());

        var held = _watcher.TakeRestoreOutputs();
        Assert.Equal(["App.csproj.nuget.g.props", "App.csproj.nuget.g.targets"], held.MSBuildFiles.Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("App", Path.GetFileName(Assert.Single(held.Directories))); // the project folder of the assets file
        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Assert.Empty(Reported()); // the restore evaluates them, not the watcher

        // With no restore running, they are reported as they come.
        File.WriteAllText(Obj("project.assets.json"), "{ }");
        await WaitUntilAsync(() => Reported().Any(c => c.Directories.Any(d => Path.GetFileName(d) == "App")));
    }

    [Fact]
    public async Task A_restore_that_fails_hands_what_it_wrote_back_to_the_watcher()
    {
        _watcher.HoldRestoreOutputs();
        WriteRestoreOutputs();
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Assert.Empty(Reported());

        _watcher.ResumeRestoreOutputs();
        await WaitUntilAsync(() => Reported().Any(c => c.MSBuildFiles.Any(f => Path.GetFileName(f) == "App.csproj.nuget.g.props")));
        Assert.Contains(Reported(), c => c.Directories.Any(d => Path.GetFileName(d) == "App"));
    }

    [Fact]
    public async Task Other_changes_are_reported_during_a_restore()
    {
        _watcher.HoldRestoreOutputs();
        File.WriteAllText(_project, "<Project><PropertyGroup /></Project>");
        await WaitUntilAsync(() => Reported().Any(c => c.MSBuildFiles.Any(f => Path.GetFileName(f) == "App.csproj")));
        Assert.Empty(_watcher.TakeRestoreOutputs().MSBuildFiles);
    }

    public void Dispose()
    {
        _watcher.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
