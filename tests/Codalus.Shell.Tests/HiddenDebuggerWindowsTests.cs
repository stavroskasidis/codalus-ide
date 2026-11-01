using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Codalus.Shell.ViewModels;
using Codalus.Workbench.Debugging;
using static Codalus.Shell.Tests.DataTipScenario;

namespace Codalus.Shell.Tests;

/// <summary>
/// The debugger's windows refreshed at each stop work only while they're shown (M56, the maintainer's choice, as the
/// classic IDE's do): hidden, Threads, Parallel Stacks, Locals, Autos, Watch and Breakpoints ask nothing of the adapter
/// (or build nothing) at a stop; shown, they catch up once, then follow each stop; picking another frame or thread moves
/// the Threads marker without asking again, and a step stops the loop over the threads. Each process is described once
/// for the session, off the UI thread (every step described it five times on the UI thread).
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
    public async Task A_window_whose_view_is_built_again_while_the_old_one_shows_it_still_refreshes_at_a_stop()
    {
        // Dock builds a tool window's view again for a new layout (a restored one, Reset Layout) while the old one still
        // shows it: the new view said it's shown, then the old one said it isn't, and with one flag for both the window
        // took itself for hidden while on screen, and stopped following the stops.
        var log = Path.Combine(Path.GetTempPath(), "codalus-hidden-windows-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            await using var s = await StartAsync(Code, Line, Stops(12), requestLog: log);
            var stops = 0;
            s.F.Shell.Debugger.Stopped += (_, _) => Interlocked.Increment(ref stops);
            int Stopped() => Volatile.Read(ref stops);
            s.F.Shell.Layout.ShowToolWindow(ThreadsViewModel.ToolWindowId, focus: false);
            await ShellFixture.PumpUntil(() => s.F.Shell.ThreadList.Rows.Count == 1 && StackTraces(log, 1) == 1);

            var second = new Avalonia.Controls.Window { Width = 400, Height = 300, Content = new Views.ThreadsView { DataContext = s.F.Shell.ThreadList } };
            second.Show();
            ShellFixture.Pump();
            s.F.Shell.Layout.CloseToolWindow(ThreadsViewModel.ToolWindowId);
            await WaitAsync(300);
            await StepAsync(s, Stopped);
            await ShellFixture.PumpUntil(() => StackTraces(log, 1) == 2, describe: () => $"{StackTraces(log, 1)} stacks read for Threads");

            // No view shows it: nothing at the next stop.
            second.Close();
            await WaitAsync(300);
            await StepAsync(s, Stopped);
            Assert.Equal(2, StackTraces(log, 1));
        }
        finally
        {
            File.Delete(log);
        }
    }

    [AvaloniaFact]
    public async Task A_refresh_the_next_step_supersedes_stops_asking_and_another_thread_moves_only_the_marker()
    {
        // Threads asks for each thread's top frame, one request after another (netcoredbg answers one at a time): a step
        // during the loop left it asking for the rest, ahead of the next stop's own requests.
        const int Threads = 60;
        var log = Path.Combine(Path.GetTempPath(), "codalus-hidden-windows-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            await using var s = await StartAsync(Code, Line, Stops(12), requestLog: log, options: new Dictionary<string, object> { ["threads"] = Threads, ["stackTraceDelay"] = 50 });
            var stops = 0;
            s.F.Shell.Debugger.Stopped += (_, _) => Interlocked.Increment(ref stops);
            s.F.Shell.Layout.ShowToolWindow(ThreadsViewModel.ToolWindowId, focus: false);
            await ShellFixture.PumpUntil(() => StackTraces(log, 1) >= 3);

            // A step while the loop asks: what it asked before the step, then the next stop's refresh, all of it.
            var before = Volatile.Read(ref stops);
            await s.F.Shell.Debugger.StepOverAsync();
            var asked = Requests(log).TakeWhile(r => r.Command != "next").Count(r => r.Command == "stackTrace" && r.Arguments.GetProperty("levels").GetInt32() == 1);
            Assert.InRange(asked, 3, Threads - 1);
            await ShellFixture.PumpUntil(() => Volatile.Read(ref stops) > before && s.F.Shell.ThreadList.Rows.Count == Threads, seconds: 60);
            await WaitAsync(500);
            Assert.InRange(StackTraces(log, 1), asked + Threads, asked + Threads + 1); // the superseded loop asked for none of the rest (one in flight at most)
            Assert.True(s.F.Shell.ThreadList.Rows.Single(r => r.IsCurrent).Id == 1);

            // Another thread: the marker moves to it; the locations stay, and no thread's stack is asked again.
            var read = StackTraces(log, 1);
            await s.F.Shell.Debugger.SelectThreadAsync(7);
            await ShellFixture.PumpUntil(() => s.F.Shell.ThreadList.Rows.SingleOrDefault(r => r.IsCurrent)?.Id == 7);
            await WaitAsync(300);
            Assert.Equal(read, StackTraces(log, 1));
            Assert.Equal("Program.Work7()", s.F.Shell.ThreadList.Rows.Single(r => r.Id == 7).Location);
        }
        finally
        {
            File.Delete(log);
        }
    }

    [AvaloniaFact]
    public async Task Breakpoints_builds_its_rows_only_while_shown_and_once_per_burst_of_changes()
    {
        // A session's start sent one setBreakpoints per file, and each answer rebuilt every row, shown or not.
        await using var f = ShellFixture.Create();
        var breakpoints = f.Shell.BreakpointList;
        var resets = 0;
        breakpoints.Rows.CollectionChanged += (_, _) => resets++;
        var file = Path.Combine(f.UserData, "Program.cs");
        for (var line = 1; line <= 50; line++)
        {
            f.Shell.Debugger.Breakpoints.Toggle(file, line);
        }

        await WaitAsync(300);
        Assert.Equal((0, 0), (resets, breakpoints.Rows.Count)); // hidden: nothing built

        f.Shell.Layout.ShowToolWindow(BreakpointsViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => breakpoints.Rows.Count == 50);
        Assert.Equal(1, resets); // once, when shown

        for (var line = 51; line <= 100; line++)
        {
            f.Shell.Debugger.Breakpoints.Toggle(file, line);
        }

        await ShellFixture.PumpUntil(() => breakpoints.Rows.Count == 100);
        await WaitAsync(300);
        Assert.Equal(2, resets); // the burst: one reset, not a rebuild (or a row added) per breakpoint
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

            // Locals behind Autos asks for nothing either; shown again, it catches up once.
            await StepAsync(s, Stopped);
            Assert.Equal(scopes + 4, Count("scopes")); // Autos' own
            Assert.Empty(s.F.Shell.Locals.Roots); // cleared as the program ran, and nothing read since
            s.F.Shell.Layout.ShowToolWindow(VariablesViewModel.LocalsWindowId, focus: false);
            await ShellFixture.PumpUntil(() => s.F.Shell.Locals.Roots.SingleOrDefault(r => r.Name == "total")?.Value == "3");
            Assert.Equal(scopes + 5, Count("scopes"));
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

    [AvaloniaFact]
    public async Task A_failed_description_is_made_again_and_the_window_title_is_read_while_the_window_is_shown()
    {
        // A description that failed left its process "being described" for the session (the toolbar kept the session's
        // name for it); the title, described with the rest at the first stop, never followed the program's window.
        var described = 0;
        var titlesRead = 0;
        var title = "Starting";
        await using var s = await StartAsync(Code, Line, Stops(12), before: f =>
        {
            f.Shell.ProcessesWindow.Describer = pid => Interlocked.Increment(ref described) == 1
                ? throw new InvalidOperationException("The process isn't ready.")
                : new ProcessRow(pid, "described", "/bin/program", string.Empty, string.Empty, string.Empty);
            f.Shell.ProcessesWindow.TitleReader = _ =>
            {
                Interlocked.Increment(ref titlesRead);
                return Volatile.Read(ref title);
            };
        }, processId: Environment.ProcessId);
        var stops = 0;
        s.F.Shell.Debugger.Stopped += (_, _) => Interlocked.Increment(ref stops);

        var expected = $"[{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}] described";
        await StepAsync(s, () => Volatile.Read(ref stops));
        await ShellFixture.PumpUntil(() => s.F.Shell.DebugLocation.Process == expected, describe: () => $"{s.F.Shell.DebugLocation.Process}, described {Volatile.Read(ref described)} times");
        await StepAsync(s, () => Volatile.Read(ref stops));
        Assert.Equal(2, Volatile.Read(ref described));
        Assert.Equal(0, Volatile.Read(ref titlesRead)); // the window isn't shown

        s.F.Shell.Layout.ShowToolWindow(ProcessesViewModel.ToolWindowId, focus: false);
        await ShellFixture.PumpUntil(() => s.F.Shell.ProcessesWindow.Rows is [{ Name: "described", Title: "Starting" }]);
        Volatile.Write(ref title, "Main window");
        await StepAsync(s, () => Volatile.Read(ref stops));
        await ShellFixture.PumpUntil(() => s.F.Shell.ProcessesWindow.Rows is [{ Title: "Main window", State: "Break" }]);
        Assert.Equal(2, Volatile.Read(ref described));
    }
}
