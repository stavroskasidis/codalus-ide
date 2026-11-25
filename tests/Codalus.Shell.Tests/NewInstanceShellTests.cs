using System.Text.Json;
using Avalonia.Headless.XUnit;
using Codalus.Debugging;
using Codalus.Shell.ViewModels;
using Codalus.Shell.Views;
using Codalus.Workbench.Context;
using Codalus.Workbench.Debugging;
using Codalus.Workbench.Projects;

namespace Codalus.Shell.Tests;

/// <summary>
/// #547 in the shell (ADR 0109), on samples/Debugging's Sessions.slnx and the real netcoredbg: Solution Explorer's
/// Debug ▸ submenu on projects that start, Start New Instance adding a project to the running F5 session, Restart
/// bringing it back, and the Processes window ending one program alone.
/// </summary>
public sealed class NewInstanceShellTests
{
    private static int Line(string file, string marker) =>
        Array.FindIndex(File.ReadAllLines(file), l => l.Contains($"// [{marker}]", StringComparison.Ordinal)) + 1;

    private static void SkipWithoutDebugger() =>
        Assert.SkipWhen(DebugAdapterLocator.Resolve() is null && Environment.GetEnvironmentVariable("CI") != "true", "netcoredbg is not installed (eng/get-netcoredbg.sh).");

    private static async Task<string> OpenSessionsAsync(ShellFixture f)
    {
        var folder = f.CopySample("Debugging");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Sessions.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 4 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        return folder;
    }

    private static ContextKeyService Project(bool startable)
    {
        var scope = new ContextKeyService();
        scope.Set("explorerItemKind", "project");
        scope.Set("explorerItemStartable", startable);
        return scope;
    }

    [AvaloniaFact]
    public async Task The_Debug_submenu_is_on_projects_that_start()
    {
        await using var f = ShellFixture.Create();
        var menus = f.Shell.Workbench.Menus;
        Assert.Contains(menus.GetItems(SolutionExplorerView.ContextMenuLocation, Project(startable: true)), i => i.Submenu == SolutionExplorerView.DebugMenuLocation && i.Title == "Debug");
        Assert.DoesNotContain(menus.GetItems(SolutionExplorerView.ContextMenuLocation, Project(startable: false)), i => i.Submenu == SolutionExplorerView.DebugMenuLocation);

        // The classic IDE's order and names.
        Assert.Equal(
            ["Start New Instance", "Start Without Debugging", "Step Into New Instance"],
            menus.GetItems(SolutionExplorerView.DebugMenuLocation, Project(startable: true)).Select(i => i.Title));
    }

    [AvaloniaFact]
    public async Task A_program_or_a_Roslyn_component_profile_is_startable()
    {
        await using var f = ShellFixture.Create();
        var folder = await OpenSessionsAsync(f);
        bool Startable() => Equals(f.Shell.Workbench.Context.GetValue("explorerItemStartable"), true);
        ExplorerRowViewModel Row(string project) => f.Shell.Explorer.Rows.First(r => r.Node is { Kind: ExplorerNodeKind.Project } n && n.ProjectPath?.EndsWith(project, StringComparison.Ordinal) == true);

        // An Exe at once; a library with a DebugRoslynComponent profile once its launch settings are read.
        f.Shell.Explorer.Selected = Row(Path.Combine("Partner", "Partner.csproj"));
        Assert.True(Startable());
        f.Shell.Explorer.Selected = Row(Path.Combine("Generator", "Generator.csproj"));
        await ShellFixture.PumpUntil(Startable, describe: () => "the generator's Roslyn component profile to make it startable");

        // The solution's row isn't a project.
        f.Shell.Explorer.Selected = f.Shell.Explorer.Rows[0];
        Assert.False(Startable());
        Assert.NotNull(folder);
    }

    [AvaloniaFact]
    public async Task Start_New_Instance_joins_the_running_session_and_Restart_brings_it_back()
    {
        SkipWithoutDebugger();
        await using var f = ShellFixture.Create();
        var folder = await OpenSessionsAsync(f);
        var debuggee = Path.Combine(folder, "Debuggee", "Program.cs");
        var partner = Path.Combine(folder, "Partner", "Program.cs");
        var partnerProject = Path.Combine(folder, "Partner", "Partner.csproj");
        f.Shell.SetStartupProject(Path.Combine(folder, "Debuggee", "Debuggee.csproj"));
        f.Shell.Debugger.Breakpoints.Toggle(debuggee, Line(debuggee, "print"));

        // F5: Debuggee alone stops at its breakpoint.
        var stopped = 0;
        f.Shell.Debugger.Stopped += (_, _) => Interlocked.Increment(ref stopped);
        await f.RunAsync("debug.start");
        await ShellFixture.PumpUntil(() => Volatile.Read(ref stopped) >= 1 && f.Shell.Debugger.State == DebugState.Stopped, seconds: 300);
        Assert.True(f.Shell.Debugger.CanAddLaunch);

        // Attach to Process and its kin add to the running session, as the classic IDE's do (#547's last step).
        await f.WhenEnabledAsync("debug.attach");
        await f.WhenEnabledAsync("debug.attachRemote");

        // Start New Instance on Partner while Debuggee is stopped: it builds and joins the session, then stops at its own
        // breakpoint once Debuggee goes on.
        f.Shell.Debugger.Breakpoints.Toggle(partner, Line(partner, "partner"));
        await f.ExecuteAsync("debug.startNewInstance", JsonSerializer.SerializeToElement(partnerProject));
        Assert.Equal([partnerProject], f.Shell.JoinedInstanceProjects);
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.Processes is [{ Id: not null }, { Id: not null }], seconds: 120, describe: () => string.Join(", ", f.Shell.Debugger.Processes.Select(p => $"{p.Name} {p.Id}")));
        await f.RunAsync("debug.continue");
        await ShellFixture.PumpUntil(
            () => Volatile.Read(ref stopped) >= 2 && f.Shell.Debugger.State == DebugState.Stopped && f.Shell.Debugger.SelectedFrame?.Source?.Path == partner,
            seconds: 120,
            describe: () => $"Partner to stop ({f.Shell.Debugger.State}, at {f.Shell.Debugger.SelectedFrame?.Source?.Path})");
        Assert.Contains(f.Shell.Debugger.Threads, t => t.Name.StartsWith("Partner: ", StringComparison.Ordinal));

        // Restart: both programs come back, Debuggee (F5's) first, then Partner joins again.
        var before = Volatile.Read(ref stopped);
        await f.RunAsync("debug.restart");
        await ShellFixture.PumpUntil(() => Volatile.Read(ref stopped) > before && f.Shell.Debugger.State == DebugState.Stopped, seconds: 300);
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.Processes.Count == 2 && f.Shell.JoinedInstanceProjects.Count == 1, seconds: 120,
            describe: () => string.Join(", ", f.Shell.Debugger.Processes.Select(p => $"{p.Name} {p.Id}")));
        Assert.Equal(["Debuggee", "Partner"], f.Shell.Debugger.Processes.Select(p => p.Name));

        // Terminate Process on Partner: it ends alone, and Restart would leave it out.
        f.Shell.Layout.ShowToolWindow(ProcessesViewModel.ToolWindowId);
        await ShellFixture.PumpUntil(
            () =>
            {
                f.Shell.ProcessesWindow.Refresh();
                return f.Shell.ProcessesWindow.Rows.Any(r => r.Name.StartsWith("Partner", StringComparison.Ordinal) && r.CanTerminate);
            },
            seconds: 120,
            describe: () => string.Join(", ", f.Shell.ProcessesWindow.Rows.Select(r => $"{r.Name} {r.Id} {r.State}")));
        var row = f.Shell.ProcessesWindow.Rows.First(r => r.Name.StartsWith("Partner", StringComparison.Ordinal));
        await f.Shell.ProcessesWindow.EndAsync(row, detach: false);
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.Processes[1].Ended, seconds: 60);
        Assert.Empty(f.Shell.JoinedInstanceProjects);
        Assert.NotEqual(DebugState.Idle, f.Shell.Debugger.State);

        await f.RunAsync("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        Assert.Empty(f.Shell.JoinedInstanceProjects);
    }
}
