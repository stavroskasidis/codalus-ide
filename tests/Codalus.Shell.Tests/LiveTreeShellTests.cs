using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Codalus.Projects.Protocol;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Previewer;

namespace Codalus.Shell.Tests;

/// <summary>
/// The Live Visual Tree and Live Property Explorer (M19b, ADR 0052) on a real Avalonia app run from the IDE: the agent
/// joins the run, the tree fills, properties show and set, and Go to XAML selects the element's tag.
/// </summary>
public sealed class LiveTreeShellTests
{
    [AvaloniaFact]
    public async Task A_running_avalonia_app_shows_its_tree_and_properties_takes_a_value_and_goes_to_its_xaml()
    {
        // The app runs windowed: Linux needs a display (CI runs the tests under Xvfb).
        Assert.SkipWhen(
            OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
            "No display: the Avalonia app can't open its window.");
        await using var f = ShellFixture.Create();
        var folder = f.CopySample("Avalonia");
        f.Shell.Trust.Trust(Path.GetDirectoryName(folder)!);
        await f.Shell.OpenSolutionAsync(Path.Combine(folder, "Avalonia.slnx"));
        await ShellFixture.PumpUntil(() => f.Shell.Solutions.Projects.Count == 2 && f.Shell.Solutions.Progress is { Completed: true }, seconds: 180);
        var app = Path.Combine(folder, "AvaloniaApp", "AvaloniaApp.csproj");
        f.Shell.SetStartupProject(app);

        // Only the plan the start launches gets a live session (2026-10-21). The dev certificate check reads the plan
        // beside the start; when its plan came last, its session replaced the launched one and stopped its listener, so
        // the app's agent got "Connection refused" and Live waited for an app that never came. The gate puts the plans
        // in that order whatever the build host's timing.
        var gate = new PlanGate(f.Shell);
        await f.Shell.StartWithoutDebuggingAsync();
        try
        {
            var connected = false;
            try
            {
                // An agent that gave up ("Codalus previewer agent: …") won't come back: fail now, not in 4 minutes.
                await ShellFixture.PumpUntil(() => (connected = f.Shell.Live is { IsConnected: true }) || Output(f, "Run").Contains("Codalus previewer agent:", StringComparison.Ordinal), seconds: 240);
            }
            catch (Xunit.Sdk.FailException)
            {
            }

            if (!connected)
            {
                // Whether the app was built, started and is still running, which sessions the start made, and what the
                // app and the build said.
                Assert.Fail($"The app's live agent didn't connect (session: {(f.Shell.Live is { } live ? $"waiting on port {live.Port}" : "none")}; "
                    + $"ProgramPlan calls: {gate.Calls} ({gate.Log}); running: "
                    + string.Join(", ", f.Shell.Runs.Running.Select(p => $"{p.Name} exited={p.HasExited}")) + ")\n"
                    + Output(f, "Run") + Output(f, "Build"));
            }

            Assert.True(gate.Calls == 1, $"One start, one plan through ProgramPlan, one live session; there were {gate.Calls}: {gate.Log}");

            var tree = f.Shell.LiveTree;
            try
            {
                await ShellFixture.PumpUntil(() => tree.Roots.Count > 0, seconds: 60);
            }
            catch (Xunit.Sdk.FailException)
            {
                Assert.Fail("The tree stayed empty: " + tree.Message + "\nRun output:\n" + string.Join("\n", f.Shell.Workbench.Output.Channels.Where(c => c.Name.StartsWith("Run", StringComparison.Ordinal)).Select(c => c.GetText())));
            }
            var window = tree.Roots[0];
            Assert.Equal("MainWindow", window.Type);
            var button = window.SelfAndDescendants().FirstOrDefault(n => n.Name == "IncrementButton");
            Assert.NotNull(button);
            Assert.DoesNotContain(window.SelfAndDescendants(), n => n.IsTemplated); // the XAML's elements only

            // The visual tree adds the templates' parts.
            tree.ShowVisualTree = true;
            await ShellFixture.PumpUntil(() => tree.Roots.Count > 0 && tree.Roots[0].SelfAndDescendants().Any(n => n.IsTemplated), seconds: 30);
            tree.ShowVisualTree = false;
            await ShellFixture.PumpUntil(() => tree.Roots.Count > 0 && !tree.Roots[0].SelfAndDescendants().Any(n => n.IsTemplated), seconds: 30);

            // Selecting shows its properties; a value set in the explorer reaches the app.
            button = tree.Roots[0].SelfAndDescendants().First(n => n.Name == "IncrementButton");
            tree.Selected = button;
            var properties = f.Shell.LiveProperties;
            await ShellFixture.PumpUntil(() => properties.Rows.Any(r => r.Name == "Content"), seconds: 30);
            Assert.Equal("Increment", properties.Rows.Single(r => r.Name == "Content").Value);
            var margin = properties.Rows.Single(r => r.Name == "Margin");
            margin.Value = "7,7,7,7";
            await ShellFixture.PumpUntil(() => properties.Rows.Any(r => r.Name == "Margin" && r.Source == "Local" && r.Value.StartsWith('7')), seconds: 30);

            // Go to XAML selects the button's start tag in MainWindow.axaml.
            await f.Shell.GoToXamlAsync(button);
            var editor = f.Shell.ActiveEditor;
            Assert.NotNull(editor);
            Assert.EndsWith("MainWindow.axaml", editor.Model.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("IncrementButton", editor.Controller!.Editor.SelectedText, StringComparison.Ordinal);
        }
        finally
        {
            f.Shell.Runs.StopAll();
            await ShellFixture.PumpUntil(() => f.Shell.Runs.Running.Count == 0, seconds: 60);
        }
    }

    private static string Output(ShellFixture f, string prefix) =>
        string.Concat(f.Shell.Workbench.Output.Channels.Where(c => c.Name.StartsWith(prefix, StringComparison.Ordinal)).Select(c => $"--- {c.Name}\n{c.GetText()}\n"));

    /// <summary>
    /// Counts the calls of the shell's <see cref="RunService.ProgramPlan"/> (the live agent) and orders two that overlap
    /// as the dev certificate check's plan and the start's did when the app couldn't connect: the first call waits, before
    /// it runs, until a second has run, and the second returns only after the first has, so the first one's session is
    /// the last made, before the program starts. With one call it waits for the dev certificate check to end instead.
    /// Calls come on the thread pool, after the build host's answers; one on the UI thread isn't held, as that would
    /// hold up the check too.
    /// </summary>
    private sealed class PlanGate
    {
        private static readonly TimeSpan s_limit = TimeSpan.FromMinutes(2);
        private readonly ShellHost _shell;
        private readonly Func<RunInfo, LaunchPlan, LaunchPlan> _addLiveAgent;
        private readonly TaskCompletionSource _secondRan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstReturned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<string> _log = new();
        private int _calls;

        public PlanGate(ShellHost shell)
        {
            _shell = shell;
            _addLiveAgent = shell.Runs.ProgramPlan!;
            shell.Runs.ProgramPlan = Plan;
        }

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Each call in the order it returned: the port of the session it made.</summary>
        public string Log => string.Join("; ", _log);

        private LaunchPlan Plan(RunInfo info, LaunchPlan plan)
        {
            var call = Interlocked.Increment(ref _calls);
            if (Dispatcher.UIThread.CheckAccess())
            {
                _log.Enqueue($"call {call} on the UI thread, not held");
                return Record(call, _addLiveAgent(info, plan));
            }

            if (call == 1)
            {
                if (Task.WaitAny([_secondRan.Task, _shell.DevCertificateCheck], s_limit) < 0)
                {
                    _log.Enqueue("call 1 waited in vain");
                }

                try
                {
                    return Record(call, _addLiveAgent(info, plan));
                }
                finally
                {
                    _firstReturned.TrySetResult();
                }
            }

            var result = Record(call, _addLiveAgent(info, plan));
            _secondRan.TrySetResult();
            if (call == 2 && !_firstReturned.Task.Wait(s_limit))
            {
                _log.Enqueue("call 2 waited in vain");
            }

            return result;
        }

        private LaunchPlan Record(int call, LaunchPlan plan)
        {
            _log.Enqueue($"call {call.ToString(CultureInfo.InvariantCulture)}: session port {plan.Environment.GetValueOrDefault(LiveSession.PortVariable, "none")}");
            return plan;
        }
    }
}
