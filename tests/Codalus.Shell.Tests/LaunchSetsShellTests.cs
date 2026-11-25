using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Build;

namespace Codalus.Shell.Tests;

/// <summary>
/// Sets of startup projects in the shell (ADR 0109), on samples/Debugging's Sessions.slnx: a launch file's set in the
/// toolbar's startup list and the Start drop-down, edited and unshared on the Startup Projects page, a change on disk,
/// and the choice kept with the solution. Nothing starts, so it needs no debugger.
/// </summary>
public sealed class LaunchSetsShellTests
{
    [AvaloniaFact]
    public async Task A_launch_files_set_is_chosen_in_the_toolbar_edited_on_the_page_and_kept_with_the_solution()
    {
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Debugging");
        var shared = Path.Combine(folder, "Sessions.slnxLaunch");
        File.WriteAllText(shared, """
            [
              {
                "Name": "Pair",
                "Projects": [
                  { "Path": "Partner\\Partner.csproj", "Action": "Start" },
                  { "Path": "Debuggee\\Debuggee.csproj", "Action": "StartWithoutDebugging" }
                ]
              }
            ]
            """);
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        var solution = Path.Combine(folder, "Sessions.slnx");
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 4 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        var toolbar = f.Shell.Toolbar;
        var partner = Path.Combine(folder, "Partner", "Partner.csproj");
        var debuggee = Path.Combine(folder, "Debuggee", "Debuggee.csproj");

        // The toolbar: the executable projects, then the set; no unnamed set yet (it has no project to start).
        await ShellFixture.PumpUntil(() => toolbar.StartupProjects.Any(c => c.Name == "Pair"), describe: () => string.Join(", ", toolbar.StartupProjects.Select(c => c.Name)));
        Assert.DoesNotContain(toolbar.StartupProjects, c => c.Name == ShellHost.MultipleStartupProjectsLabel);
        Assert.Equal("Pair", toolbar.StartupProjects[^1].Name);
        Assert.False(f.Shell.Runs.MultipleStartup);

        // Choosing it: Start starts its two projects in order, the button says its name, the drop-down offers the page.
        toolbar.StartupProject = toolbar.StartupProjects.Single(c => c.Name == "Pair");
        Assert.Equal("Pair", f.Shell.Runs.ActiveLaunchSet?.Name);
        Assert.Equal([new StartupEntry(partner, StartupAction.Start), new StartupEntry(debuggee, StartupAction.StartWithoutDebugging)], f.Shell.Runs.StartupTargets);
        await ShellFixture.PumpUntil(() => toolbar.StartLabel == "Pair" && toolbar.IsStartupSet);
        var view = f.Window.GetVisualDescendants().OfType<StandardToolbar>().Single();
        await ShellFixture.PumpUntil(() => view.LaunchMenu.Items.OfType<MenuItem>().Any(i => i.Command == toolbar.ConfigureStartupProjectsCommand));
        Assert.DoesNotContain(view.LaunchMenu.Items.OfType<MenuItem>(), i => i.Command == toolbar.SelectLaunchProfileCommand);

        // The page shows the set; Debuggee is debugged too, and the set unshared: it moves to the .user file.
        await f.RunAsync("run.configureStartupProjects");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<StartupProjectsView>().Any());
        var page = f.Shell.Layout.Documents.OfType<SolutionManagementDocument>().Select(d => d.ViewModel).OfType<StartupProjectsViewModel>().Single();
        await ShellFixture.PumpUntil(() => page.Rows.Count == 4 && page.SelectedSet?.Name == "Pair");
        Assert.True(page.IsMultiple);
        Assert.True(page.IsShared);
        Assert.Equal(["Partner", "Debuggee"], page.Rows.Take(2).Select(r => r.Name));
        Assert.Equal([StartupAction.Start, StartupAction.StartWithoutDebugging, StartupAction.None, StartupAction.None], page.Rows.Select(r => r.Action));
        page.Rows[1].Action = StartupAction.Start;
        page.IsShared = false;
        page.SetName = "Both";
        await page.ApplyAsync();
        Assert.False(File.Exists(shared), page.Status);
        Assert.Contains("\"Name\": \"Both\"", File.ReadAllText(shared + ".user"), StringComparison.Ordinal);
        Assert.Equal("Both", f.Shell.Runs.ActiveLaunchSet?.Name);
        Assert.All(f.Shell.Runs.StartupTargets, t => Assert.Equal(StartupAction.Start, t.Action));
        await ShellFixture.PumpUntil(() => toolbar.StartLabel == "Both" && toolbar.StartupProjects.All(c => c.Name != "Pair"));

        // The unnamed set is still there: chosen on the page, it's the toolbar's <Multiple Startup Projects>.
        page.SelectedSet = page.Sets[0];
        page.Rows.Single(r => r.Name == "Debuggee").Action = StartupAction.Start;
        await page.ApplyAsync();
        Assert.True(f.Shell.Runs.MultipleStartup);
        Assert.Null(f.Shell.Runs.ActiveLaunchSet);
        await ShellFixture.PumpUntil(() => toolbar.StartLabel == ShellHost.MultipleStartupProjectsLabel);

        // A set added on disk (the classic IDE, a checkout) shows up in the toolbar.
        File.WriteAllText(shared, """[{ "Name": "Theirs", "Projects": [{ "Path": "Partner\\Partner.csproj", "Action": "Start" }] }]""");
        await ShellFixture.PumpUntil(() => toolbar.StartupProjects.Any(c => c.Name == "Theirs"), seconds: 30);

        // Kept with the solution: reopened, Start starts the named set again.
        f.Shell.SelectLaunchSet("Both");
        f.Shell.SaveEditorSession();
        await f.Shell.CloseSolutionAsync();
        Assert.Empty(f.Shell.Runs.LaunchSets);
        await f.Shell.OpenSolutionAsync(solution);
        await ShellFixture.PumpUntil(() => f.Shell.Runs.ActiveLaunchSet?.Name == "Both", seconds: 120);
        Assert.Equal(2, f.Shell.Runs.StartupTargets.Count);
    }
}
