using System.Reflection;
using System.Text.Json;
using Codalus.Debugging;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// Break All shows a stack. netcoredbg pauses the thread it is asked to (the first it lists, by the lowest thread id),
/// stops them all, and names that thread in its stopped event; on Windows it can be a runtime thread without managed
/// frames, whose stack is empty or unreadable (the packaged WinUI app attached to). The stop then shows the first thread
/// with the program's own code, or else the first with frames. The adapter is the scripted one
/// (<c>Codalus.LspConformance.Server --dap</c>), given the program's threads.
/// </summary>
public sealed class BreakAllTests
{
    private static string Metadata(string key) =>
        typeof(BreakAllTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string ScriptedAdapter => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.LspConformance.Server", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    private static readonly string s_program = Path.Combine(Path.GetTempPath(), "App", "Program.cs");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token).Token;

    private static object Thread(int id, string name, params object[] frames) => new { id, name, frames };

    /// <summary>A thread's id as the adapter has it: the session's encode the program's place (#547: every session is one of programs).</summary>
    private static int? Shown(DebugService debug, int? threadId) => threadId is { } id ? debug.DisplayThreadId(id) : null;

    /// <summary>A thread whose stack netcoredbg can't read.</summary>
    private static object Unreadable(int id, string name) => new { id, name, frames = "error" };

    private static object Frame(string name) => new { name };

    private static object Frame(string name, int line) => new { name, path = s_program, line };

    /// <summary>A runtime thread first (no managed frames), a thread pool thread in framework code, the main thread with the program's code.</summary>
    private static object[] Program(object first) =>
    [
        first,
        Thread(2, ".NET TP Worker", Frame("System.Threading.WaitHandle.WaitOneNoCheck"), Frame("System.Threading.PortableThreadPool.WorkerThread.WorkerThreadStart")),
        Thread(3, "Main Thread", Frame("[Native Frames]"), Frame("Microsoft.UI.Xaml.Application.Start"), Frame("App.Program.Main", 12)),
    ];

    private static async Task<DebugService> PausedAsync(object[] threads, bool pauseNamesNoThread = false, System.Text.StringBuilder? console = null, bool continueEnds = false)
    {
        var debug = new DebugService(() => null);
        var stopped = new TaskCompletionSource<StopInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Stopped += (_, stop) => stopped.TrySetResult(stop);
        var output = console ?? new System.Text.StringBuilder();
        debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
        var launch = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["threads"] = threads, ["pauseNamesNoThread"] = pauseNamesNoThread, ["continueEnds"] = continueEnds });
        await debug.LaunchAdapterAsync("Scripted", new DebugAdapterStartInfo("dotnet", [ScriptedAdapter, "--dap"], AppContext.BaseDirectory), launch, Timeout());
        Assert.Equal(DebugState.Running, debug.State);

        await debug.PauseAsync();
        try
        {
            await stopped.Task.WaitAsync(Timeout());
        }
        catch (OperationCanceledException)
        {
            lock (output)
            {
                Assert.Fail($"The program didn't stop. State: {debug.State}. Debug Console:\n{output}");
            }
        }

        Assert.Equal(DebugState.Stopped, debug.State);
        return debug;
    }

    [Fact]
    public async Task A_paused_thread_without_frames_shows_the_thread_with_the_programs_code()
    {
        await using var debug = await PausedAsync(Program(Thread(1, "<No name>")));

        Assert.Equal(3, Shown(debug, debug.SelectedThreadId));
        Assert.Equal(3, Shown(debug, debug.CurrentStop?.ThreadId));
        Assert.Equal(["[Native Frames]", "Microsoft.UI.Xaml.Application.Start", "App.Program.Main"], debug.Frames.Select(f => f.Name));
        Assert.Equal("App.Program.Main", debug.SelectedFrame?.Name);
        Assert.Equal([1, 2, 3], debug.Threads.Select(t => debug.DisplayThreadId(t.Id)));
    }

    [Fact]
    public async Task A_paused_thread_whose_stack_cant_be_read_shows_another()
    {
        await using var debug = await PausedAsync(Program(Unreadable(1, "<No name>")));

        Assert.Equal(3, Shown(debug, debug.SelectedThreadId));
        Assert.Equal("App.Program.Main", debug.SelectedFrame?.Name);
    }

    [Fact]
    public async Task Without_the_programs_code_the_first_thread_with_frames_shows()
    {
        // An attached app whose threads are all framework code.
        await using var debug = await PausedAsync(
        [
            Thread(1, "<No name>"),
            Thread(2, ".NET TP Worker", Frame("System.Threading.WaitHandle.WaitOneNoCheck")),
            Thread(3, "Main Thread", Frame("[Native Frames]"), Frame("Microsoft.UI.Xaml.Application.Start")),
        ]);

        Assert.Equal(2, Shown(debug, debug.SelectedThreadId));
        Assert.Equal(["System.Threading.WaitHandle.WaitOneNoCheck"], debug.Frames.Select(f => f.Name));
        Assert.Equal("System.Threading.WaitHandle.WaitOneNoCheck", debug.SelectedFrame?.Name);
    }

    [Fact]
    public async Task A_paused_thread_with_the_programs_code_stays()
    {
        await using var debug = await PausedAsync(
        [
            Thread(1, "Main Thread", Frame("System.Threading.Thread.Sleep"), Frame("App.Program.Main", 7)),
            Thread(2, "Worker", Frame("App.Worker.Run", 20)),
        ]);

        Assert.Equal(1, Shown(debug, debug.SelectedThreadId));
        Assert.Equal("App.Program.Main", debug.SelectedFrame?.Name);
    }

    [Fact]
    public async Task A_pause_where_no_thread_has_frames_is_a_stop_in_external_code()
    {
        // The packaged WinUI app attached to as it starts (PR #29's Windows CI): paused before its Main ran, no thread has
        // managed frames, the runtime's empty and the others' unreadable. It's still a stop: the thread the event named
        // shows, without frames (the Call Stack shows [External Code]), the Debug Console says why, and Continue runs on.
        var console = new System.Text.StringBuilder();
        await using var debug = await PausedAsync([Thread(1, "<No name>"), Unreadable(2, ".NET Finalizer"), Thread(3, "Main Thread")], console: console);

        Assert.Equal(1, Shown(debug, debug.SelectedThreadId));
        Assert.Equal(1, Shown(debug, debug.CurrentStop?.ThreadId));
        Assert.Equal("pause", debug.CurrentStop?.Reason);
        Assert.Empty(debug.Frames);
        Assert.Null(debug.SelectedFrame);
        Assert.Equal([1, 2, 3], debug.Threads.Select(t => debug.DisplayThreadId(t.Id)));
        lock (console)
        {
            Assert.Contains("Paused in external code: none of the program's 3 threads is running .NET code", console.ToString(), StringComparison.Ordinal);
        }

        await debug.ContinueAsync();
        Assert.Equal(DebugState.Running, debug.State);
    }

    [Fact]
    public async Task A_pause_that_names_no_thread_where_none_has_frames_shows_the_first()
    {
        await using var debug = await PausedAsync([Thread(4, "<No name>"), Thread(5, "Main Thread")], pauseNamesNoThread: true);

        Assert.Equal(4, Shown(debug, debug.SelectedThreadId));
        Assert.Empty(debug.Frames);
    }

    [Fact]
    public async Task A_stop_that_names_no_thread_shows_one()
    {
        await using var debug = await PausedAsync(Program(Thread(1, "<No name>")), pauseNamesNoThread: true);

        Assert.Equal(3, Shown(debug, debug.SelectedThreadId));
        Assert.Equal("App.Program.Main", debug.SelectedFrame?.Name);
    }

    [Fact]
    public async Task Continue_ends_quietly_when_the_program_ends_before_the_adapter_answers()
    {
        // The session's end cancelled the continue still waiting for its answer, and Continue threw that cancellation to
        // its caller (the command): netcoredbg on macOS reported a program's end before its answer to the continue that
        // ran it there (DebugServiceTests.Hit_counts_are_emulated_and_the_program_runs_to_its_exit_code, PR #518's run
        // 45089886744). The scripted adapter never answers it.
        await using var debug = await PausedAsync(Program(Thread(1, "<No name>")), continueEnds: true);
        var ended = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Ended += (_, code) => ended.TrySetResult(code);

        await debug.ContinueAsync().WaitAsync(Timeout());
        Assert.Equal(15, await ended.Task.WaitAsync(Timeout()));
        Assert.Equal(DebugState.Idle, debug.State);
    }
}
