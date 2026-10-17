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

    private static async Task<DebugService> PausedAsync(object[] threads, bool pauseNamesNoThread = false)
    {
        var debug = new DebugService(() => null);
        var stopped = new TaskCompletionSource<StopInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Stopped += (_, stop) => stopped.TrySetResult(stop);
        var output = new System.Text.StringBuilder();
        debug.Output += (_, o) => { lock (output) { output.Append(o.Text); } };
        var launch = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["threads"] = threads, ["pauseNamesNoThread"] = pauseNamesNoThread });
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

        Assert.Equal(3, debug.SelectedThreadId);
        Assert.Equal(3, debug.CurrentStop?.ThreadId);
        Assert.Equal(["[Native Frames]", "Microsoft.UI.Xaml.Application.Start", "App.Program.Main"], debug.Frames.Select(f => f.Name));
        Assert.Equal("App.Program.Main", debug.SelectedFrame?.Name);
        Assert.Equal([1, 2, 3], debug.Threads.Select(t => t.Id));
    }

    [Fact]
    public async Task A_paused_thread_whose_stack_cant_be_read_shows_another()
    {
        await using var debug = await PausedAsync(Program(Unreadable(1, "<No name>")));

        Assert.Equal(3, debug.SelectedThreadId);
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

        Assert.Equal(2, debug.SelectedThreadId);
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

        Assert.Equal(1, debug.SelectedThreadId);
        Assert.Equal("App.Program.Main", debug.SelectedFrame?.Name);
    }

    [Fact]
    public async Task A_stop_that_names_no_thread_shows_one()
    {
        await using var debug = await PausedAsync(Program(Thread(1, "<No name>")), pauseNamesNoThread: true);

        Assert.Equal(3, debug.SelectedThreadId);
        Assert.Equal("App.Program.Main", debug.SelectedFrame?.Name);
    }
}
