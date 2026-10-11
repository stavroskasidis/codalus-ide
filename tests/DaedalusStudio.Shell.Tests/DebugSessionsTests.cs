using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using DaedalusStudio.Debugging;
using DaedalusStudio.Shell.ViewModels;
using DaedalusStudio.Shell.Views;
using DaedalusStudio.Workbench.Build;
using DaedalusStudio.Workbench.Debugging;
using DaedalusStudio.Workbench.Settings;

namespace DaedalusStudio.Shell.Tests;

/// <summary>
/// M14c in the shell (ADR 0039), on samples/Debugging's Sessions.slnx and the real netcoredbg: the Startup Projects page
/// and a compound launch of two projects, a program debugged in the integrated terminal with Detach All, the Roslyn
/// component profile, and Just-In-Time debugging against a stand-in user environment.
/// </summary>
public sealed class DebugSessionsTests
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

    [AvaloniaFact]
    public async Task Two_startup_projects_from_the_Startup_Projects_page_debug_in_one_session()
    {
        SkipWithoutDebugger();
        await using var f = ShellFixture.Create();
        var folder = await OpenSessionsAsync(f);
        var debuggee = Path.Combine(folder, "Debuggee", "Program.cs");
        var partner = Path.Combine(folder, "Partner", "Program.cs");

        // The page: multiple startup projects, Debuggee and Partner started, Partner first.
        f.Shell.Run("run.configureStartupProjects");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<StartupProjectsView>().Any());
        var page = f.Shell.Layout.Documents.OfType<SolutionManagementDocument>().Select(d => d.ViewModel).OfType<StartupProjectsViewModel>().Single();
        await ShellFixture.PumpUntil(() => page.Rows.Count == 4);
        page.IsMultiple = true;
        page.Rows.Single(r => r.Name == "Debuggee").Action = StartupAction.Start;
        var partnerRow = page.Rows.Single(r => r.Name == "Partner");
        partnerRow.ActionIndex = 1;
        page.Selected = partnerRow;
        while (page.Rows.IndexOf(partnerRow) > 0)
        {
            page.Move(-1);
        }

        page.Apply();
        Assert.True(f.Shell.Runs.MultipleStartup);
        Assert.Equal(["Partner", "Debuggee"], f.Shell.Runs.StartupTargets.Select(t => Path.GetFileNameWithoutExtension(t.Project)));

        // F5: both build and start; each stops at its breakpoint.
        f.Shell.Debugger.Breakpoints.Toggle(debuggee, Line(debuggee, "print"));
        f.Shell.Debugger.Breakpoints.Toggle(partner, Line(partner, "partner"));
        f.Shell.Run("debug.start");
        var stops = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 300);
            stops.Add(Path.GetFileName(Path.GetDirectoryName(f.Shell.Debugger.SelectedFrame?.Source?.Path) ?? string.Empty));
            if (i == 0)
            {
                // The Processes window lists both programs of the session.
                f.Shell.Layout.ShowToolWindow(ProcessesViewModel.ToolWindowId);
                await ShellFixture.PumpUntil(() => f.Shell.ProcessesWindow.Rows.Count == 2);
                Assert.Contains(f.Shell.ProcessesWindow.Rows, r => r.Kind == "Launched");
                Assert.False(f.Shell.ProcessesWindow.CanDetach);
            }

            await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("debugStopped"), true)); // the command's enablement
            f.Shell.Run("debug.continue");
            await ShellFixture.PumpUntil(() => f.Shell.Debugger.State != DebugState.Stopped || i == 1);
        }

        Assert.Equal(["Debuggee", "Partner"], stops.Order());
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 120);
        Assert.Contains("[Partner] The program has exited with code", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
        Assert.Contains("[Debuggee] The program has exited with code", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);

        // Saved with the solution: reopening it brings the startup projects back.
        f.Shell.SaveEditorSession();
        await f.Shell.CloseSolutionAsync();
        f.Shell.Runs.Load(null, null);
        Assert.False(f.Shell.Runs.MultipleStartup);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Sessions.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Runs.MultipleStartup, seconds: 60);
        Assert.Equal(["Partner", "Debuggee"], f.Shell.Runs.StartupTargets.Select(t => Path.GetFileNameWithoutExtension(t.Project)));
    }

    [AvaloniaFact]
    public async Task A_console_app_debugged_in_the_terminal_takes_input_and_runs_on_after_Detach_All()
    {
        SkipWithoutDebugger();
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Debugging");
        var properties = Path.Combine(folder, "Debuggee", "Properties");
        Directory.CreateDirectory(properties);
        await File.WriteAllTextAsync(Path.Combine(properties, "launchSettings.json"), """
            { "profiles": { "Read": { "commandName": "Project", "commandLineArgs": "--read" } } }
            """, TestContext.Current.CancellationToken);
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Debugging.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 1 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 120);
        await f.Shell.Workbench.Settings.SetAsync(ShellHost.DebugConsoleSetting, "integratedTerminal", SettingScope.User, CancellationToken.None);
        var program = Path.Combine(folder, "Debuggee", "Program.cs");
        f.Shell.Debugger.Breakpoints.Toggle(program, Line(program, "loop"));

        f.Shell.Run("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.DebugTerminal.Last is not null, seconds: 300);
        var session = f.Shell.DebugTerminal.Last!;
        await ShellFixture.PumpUntil(() => f.Shell.TerminalWindow.Tabs.Any(t => t.Primary == session));
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.Processes is [{ Kind: DebugProcess.Terminal, Id: not null }], seconds: 120);
        session.Write("typed in the terminal\r");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 120);
        string Screen()
        {
            lock (session.Terminal.SyncRoot)
            {
                return string.Join('\n', Enumerable.Range(0, session.Terminal.TotalRows).Select(i => session.Terminal.GetRow(i).GetText()));
            }
        }

        Assert.Contains("echo: typed in the terminal", Screen(), StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => Equals(f.Shell.Workbench.Context.GetValue("debugCanDetach"), true));
        Assert.True(f.Shell.Workbench.Commands.IsEnabled("debug.detachAll"));

        f.Shell.Run("debug.detachAll");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
        await ShellFixture.PumpUntil(() => session.ExitCode is not null, seconds: 120);
        Assert.True(session.ExitCode == 15, $"exit code {session.ExitCode}; terminal:\n{Screen()}\nDebug Console:\n{f.Shell.DebugLog.GetText()}");
        Assert.Contains("done", Screen(), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task F5_on_a_source_generator_runs_the_compiler_on_its_target_and_stops_in_the_generator()
    {
        SkipWithoutDebugger();
        await using var f = ShellFixture.Create();
        var folder = await OpenSessionsAsync(f);
        var generator = Path.Combine(folder, "Generator", "HelloGenerator.cs");
        f.Shell.SetStartupProject(Path.Combine(folder, "Generator", "Generator.csproj"));
        f.Shell.Debugger.Breakpoints.Toggle(generator, Line(generator, "generate"));

        f.Shell.Run("debug.start");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Stopped, seconds: 300);
        Assert.Equal(generator, f.Shell.Debugger.SelectedFrame?.Source?.Path, ignoreCase: OperatingSystem.IsWindows());
        Assert.Contains("csc.dll", f.Shell.DebugLog.GetText(), StringComparison.Ordinal);
        await ShellFixture.PumpUntil(() => f.Shell.Locals.Roots.Any(r => r.Name == "list" && r.Value.Contains("Customer", StringComparison.Ordinal)));
        f.Shell.Run("debug.stop");
        await ShellFixture.PumpUntil(() => f.Shell.Debugger.State == DebugState.Idle, seconds: 60);
    }

    private sealed class StandInEnvironment : IUserEnvironment
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        public string Description => "a test's stand-in environment";

        public IReadOnlyDictionary<string, string> Read(IReadOnlyCollection<string> names) =>
            Values.Where(p => names.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        public void Write(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (name, value) in values)
            {
                if (value is null)
                {
                    Values.Remove(name);
                }
                else
                {
                    Values[name] = value;
                }
            }
        }
    }

    [AvaloniaFact]
    public async Task Just_In_Time_debugging_offers_a_crashed_programs_dump()
    {
        await using var f = ShellFixture.Create();
        var environment = new StandInEnvironment();
        var dumps = Path.Combine(f.UserData, "crash-dumps");
        f.Shell.Jit = new JitDebugging(environment, dumps);

        // The dialog says what changes; Turn On registers in the stand-in environment only.
        var configuring = f.Shell.ConfigureJitDebuggingAsync();
        await ShellFixture.PumpUntil(() => f.Shell.Dialogs.IsOpen);
        Assert.Contains("a test's stand-in environment", f.Shell.Dialogs.Message, StringComparison.Ordinal);
        f.Shell.Dialogs.Accept();
        await configuring;
        Assert.True(f.Shell.Jit.IsRegistered);

        // A program that crashes with those variables writes a dump, and the shell offers it.
        var folder = f.CopySample("Debugging");
        var build = RunDotnet(["build", Path.Combine(folder, "Debugging.slnx"), "-nologo", "-v", "quiet"], null);
        Assert.True(build.Code == 0, build.Output);
        var crash = RunDotnet(["exec", Path.Combine(folder, "Debuggee", "bin", "Debug", "net10.0", "Debuggee.dll"), "--crash"], environment.Values);
        Assert.NotEqual(0, crash.Code);
        Assert.True(Directory.GetFiles(dumps, "*.dmp").Length == 1, $"the program wrote a dump into {dumps}:\n{crash.Output}\nVariables: {string.Join(", ", environment.Values)}");
        try
        {
            await ShellFixture.PumpUntil(() => f.Shell.Workbench.Notifications.Items.Any(n => n.Message.Contains("crashed and wrote a dump", StringComparison.Ordinal)), seconds: 120);
        }
        catch (Xunit.Sdk.TrueException)
        {
            Assert.Fail($"No crash notification. Notifications: {string.Join(" | ", f.Shell.Workbench.Notifications.Items.Select(n => n.Message))}\nLog:\n{f.Shell.Workbench.Log.GetText()}");
        }

        var notification = f.Shell.Workbench.Notifications.Items.First(n => n.Message.Contains("crashed and wrote a dump", StringComparison.Ordinal));
        f.Shell.Workbench.Notifications.Choose(notification, "Open Dump");
        await ShellFixture.PumpUntil(() => f.Window.GetVisualDescendants().OfType<DumpView>().Any(), seconds: 120);

        await f.Shell.ConfigureJitDebuggingAsync("off");
        Assert.False(f.Shell.Jit.IsRegistered);
        Assert.Empty(environment.Values);
    }

    private static (int Code, string Output) RunDotnet(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environment)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet", arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, output.Result + error.Result);
    }
}
