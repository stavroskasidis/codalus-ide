using Avalonia.Headless.XUnit;

namespace Codalus.Shell.Tests;

/// <summary>Running tasks in the status bar (M22a): their text, a progress bar, cancel, and the IDE's own tasks.</summary>
public sealed class ProgressStatusTests
{
    [AvaloniaFact]
    public async Task A_task_shows_its_text_and_progress_and_can_be_cancelled()
    {
        await using var f = ShellFixture.Create();
        var bar = f.Shell.StatusBar;
        Assert.False(bar.HasProgress);
        Assert.Equal("Ready", bar.DisplayMessage);

        var cancelled = false;
        var task = f.Shell.Workbench.Progress.Begin("Indexing", "Test", () => cancelled = true);
        await ShellFixture.PumpUntil(() => bar.HasProgress);
        Assert.Equal("Indexing…", bar.DisplayMessage);
        Assert.True(bar.IsProgressIndeterminate);
        Assert.True(bar.CanCancelProgress);

        task.Report("Indexing file 3 of 4: Program.cs", 75);
        await ShellFixture.PumpUntil(() => bar.DisplayMessage == "Indexing file 3 of 4: Program.cs");
        Assert.False(bar.IsProgressIndeterminate);
        Assert.Equal(75, bar.ProgressValue);

        // A second task: the newest shows, with the count of the others.
        var other = f.Shell.Workbench.Progress.Begin("Waiting", "Test");
        await ShellFixture.PumpUntil(() => bar.DisplayMessage == "Waiting…  (+1 more)");
        Assert.True(bar.CanCancelProgress); // the first one still can be
        other.End();

        bar.CancelProgressCommand!.Execute(null);
        Assert.True(cancelled);
        await ShellFixture.PumpUntil(() => bar.DisplayMessage.EndsWith("(cancelling)", StringComparison.Ordinal));
        task.End();
        await ShellFixture.PumpUntil(() => !bar.HasProgress && bar.DisplayMessage == "Ready");
    }

    [AvaloniaFact]
    public async Task Loading_projects_and_building_are_tasks_in_the_status_bar()
    {
        await using var f = ShellFixture.Create();
        var seen = new List<string>();
        f.Shell.Workbench.Progress.Changed += (_, _) =>
        {
            lock (seen)
            {
                seen.AddRange(f.Shell.Workbench.Progress.Running.Select(t => t.Message ?? t.Title));
            }
        };

        var folder = f.CopySample("BuildAndRun");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Directory.EnumerateFiles(folder, "*.sln*").First());
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Progress is { Completed: true } && f.Shell.Solutions.Projects.Count == 2, seconds: 240);

        // The project the load is on, as the status bar said it (the progress arrives on the UI thread after the event).
        bool Said()
        {
            lock (seen)
            {
                return seen.Any(s => s.StartsWith("Loading project ", StringComparison.Ordinal) && s.Contains(" of 2: ", StringComparison.Ordinal) && s.EndsWith("proj", StringComparison.Ordinal));
            }
        }

        await ShellFixture.PumpUntil(Said);

        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Progress.Running.All(t => t.Title != "Loading projects"));

        // A build is a cancellable task while it runs.
        var before = f.Shell.Builds.LastBuild;
        f.Shell.Run("build.buildSolution");
        await ShellFixture.PumpUntil(() => f.Shell.StatusBar.HasProgress && f.Shell.StatusBar.DisplayMessage.StartsWith("Building ", StringComparison.Ordinal) || f.Shell.Builds.LastBuild != before, seconds: 120);
        if (f.Shell.Builds.LastBuild == before)
        {
            Assert.True(f.Shell.StatusBar.CanCancelProgress);
        }

        await ShellFixture.PumpUntil(() => f.Shell.Builds.LastBuild is { } b && b != before, seconds: 300);
        await ShellFixture.PumpUntil(() => f.Shell.Workbench.Progress.Running.All(t => t.Source != "Build"));
    }
}
