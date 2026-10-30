using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Debugging;
using static Codalus.Shell.Tests.DataTipScenario;

namespace Codalus.Shell.Tests;

/// <summary>
/// The debugger's windows refreshed at each stop work only while they're shown (M56, the maintainer's choice, as the
/// classic IDE's do): hidden, Threads and Parallel Stacks ask nothing of the adapter at a stop; shown, they catch up
/// once, then follow each stop; picking another frame moves the Threads marker without asking again. Each process is
/// described once for the session, off the UI thread (every step described it five times on the UI thread).
/// </summary>
public sealed class HiddenDebuggerWindowsTests
{
    private const string Code = "class Program\n{\n    static void Main()\n    {\n        Use(total);\n    }\n}\n";

    private const int Line = 5;

    private static object[] Stops(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i => (object)new Dictionary<string, object>
        {
            ["variables"] = new Dictionary<string, object> { ["1"] = new object[] { Var("total", i.ToString(CultureInfo.InvariantCulture), "int", 0, "total") } },
        }),
    ];

    /// <summary>The stackTrace requests for <paramref name="levels"/> frames: 1 for Threads, 64 for Parallel Stacks (the stop's own asks for 200).</summary>
    private static int StackTraces(string log, int levels) =>
        Requests(log).Count(r => r.Command == "stackTrace" && r.Arguments.TryGetProperty("levels", out var l) && l.GetInt32() == levels);

    private static async Task WaitAsync(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        await ShellFixture.PumpUntil(() => DateTime.UtcNow >= until);
    }

    /// <summary>Steps to the next stop, and gives what it posts time to run (and to ask the adapter, if it would).</summary>
    private static async Task StepAsync(DataTipScenario s, Func<int> stops)
    {
        var before = stops();
        await s.F.Shell.Debugger.StepOverAsync();
        await ShellFixture.PumpUntil(() => stops() > before && s.F.Shell.Debugger.State == DebugState.Stopped, seconds: 60);
        await WaitAsync(300);
    }

    [AvaloniaFact]
    public async Task Threads_and_Parallel_Stacks_ask_for_the_threads_stacks_only_while_shown()
    {
        var log = Path.Combine(Path.GetTempPath(), "codalus-hidden-windows-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            await using var s = await StartAsync(Code, Line, Stops(12), requestLog: log);
            var stops = 0;
            s.F.Shell.Debugger.Stopped += (_, _) => Interlocked.Increment(ref stops);
            int Stopped() => Volatile.Read(ref stops);

            // Hidden (never opened): a stop asks for no thread's stack on their behalf.
            for (var i = 0; i < 3; i++)
            {
                await StepAsync(s, Stopped);
            }

            Assert.Equal((0, 0), (StackTraces(log, 1), StackTraces(log, 64)));

            // Shown: they catch up once, then follow each stop.
            s.F.Shell.Layout.ShowToolWindow(ThreadsViewModel.ToolWindowId, focus: false);
            await ShellFixture.PumpUntil(() => s.F.Shell.ThreadList.Rows.Count == 1);
            Assert.Equal(1, StackTraces(log, 1));
            Assert.True(s.F.Shell.ThreadList.Rows[0].IsCurrent);
            await StepAsync(s, Stopped);
            await ShellFixture.PumpUntil(() => StackTraces(log, 1) == 2);

            // Another frame: the marker follows the thread; the locations stay, and nothing is asked again.
            s.F.Shell.Debugger.SelectFrame(s.F.Shell.Debugger.Frames[0]);
            await WaitAsync(300);
            Assert.Equal(2, StackTraces(log, 1));
            Assert.True(s.F.Shell.ThreadList.Rows[0].IsCurrent);

            // Closed: nothing again.
            s.F.Shell.Layout.CloseToolWindow(ThreadsViewModel.ToolWindowId);
            await WaitAsync(300);
            await StepAsync(s, Stopped);
            await StepAsync(s, Stopped);
            Assert.Equal(2, StackTraces(log, 1));

            s.F.Shell.Layout.ShowToolWindow(ParallelStacksViewModel.ToolWindowId, focus: false);
            await ShellFixture.PumpUntil(() => s.F.Shell.ParallelStacksWindow.Roots.Count == 1);
            Assert.Equal(1, StackTraces(log, 64));
        }
        finally
        {
            File.Delete(log);
        }
    }

    [AvaloniaFact]
    public async Task Autos_and_Watch_evaluate_nothing_at_a_stop_while_hidden_and_catch_up_when_shown()
    {
        var log = Path.Combine(Path.GetTempPath(), "codalus-hidden-windows-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            await using var s = await StartAsync(Code, Line, Stops(12), requestLog: log);
            var stops = 0;
            s.F.Shell.Debugger.Stopped += (_, _) => Interlocked.Increment(ref stops);
            int Stopped() => Volatile.Read(ref stops);
            int Count(string command) => Requests(log).Count(r => r.Command == command);
            s.F.Shell.Layout.ShowToolWindow(VariablesViewModel.LocalsWindowId, focus: false);
            await ShellFixture.PumpUntil(() => s.F.Shell.Locals.Roots.Any(r => r.Name == "total"));
            await s.F.Shell.Watch.AddAsync("total"); // evaluated once, as it's added
            await WaitAsync(300);
            var (scopes, evaluations) = (Count("scopes"), Count("evaluate"));

            // Locals reads its scope at each stop; Autos and Watch, behind it or closed, ask for nothing.
            await StepAsync(s, Stopped);
            await StepAsync(s, Stopped);
            Assert.Equal((scopes + 2, evaluations), (Count("scopes"), Count("evaluate")));

            // Shown, each catches up once.
            s.F.Shell.Layout.ShowToolWindow(WatchViewModel.ToolWindowId, focus: false);
            await ShellFixture.PumpUntil(() => Count("evaluate") == evaluations + 1 && s.F.Shell.Watch.Roots.Single().Value == "2");
            s.F.Shell.Layout.ShowToolWindow(VariablesViewModel.AutosWindowId, focus: false);
            await ShellFixture.PumpUntil(() => s.F.Shell.Autos.Roots.Any(r => r.Name == "total"));
            Assert.Equal(scopes + 3, Count("scopes"));
        }
        finally
        {
            File.Delete(log);
        }
    }

    [AvaloniaFact]
    public async Task Each_process_is_described_once_for_the_session_and_off_the_UI_thread()
    {
        var described = 0;
        var onUiThread = false;
        await using var s = await StartAsync(Code, Line, Stops(12), before: f => f.Shell.ProcessesWindow.Describer = pid =>
        {
            Interlocked.Increment(ref described);
            onUiThread |= Dispatcher.UIThread.CheckAccess();
            return new ProcessRow(pid, "described", "/bin/program", string.Empty, string.Empty, string.Empty);
        }, processId: Environment.ProcessId);
        var stops = 0;
        s.F.Shell.Debugger.Stopped += (_, _) => Interlocked.Increment(ref stops);

        // The Debug Location toolbar shows the process's name, described once, however many times the program stops.
        var expected = $"[{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}] described";
        await ShellFixture.PumpUntil(() => s.F.Shell.DebugLocation.Process == expected, describe: () => s.F.Shell.DebugLocation.Process);
        for (var i = 0; i < 10; i++)
        {
            await StepAsync(s, () => Volatile.Read(ref stops));
        }

        s.F.Shell.Layout.ShowToolWindow(ProcessesViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => s.F.Shell.ProcessesWindow.Rows is [{ Name: "described", State: "Break", Path: "/bin/program" }]);
        Assert.Equal(expected, s.F.Shell.DebugLocation.Process);
        Assert.Equal(1, Volatile.Read(ref described));
        Assert.False(onUiThread);
    }
}
