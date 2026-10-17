using System.Globalization;
using System.Text;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// A program whose runtime netcoredbg sees only after its fixed 5 seconds (a loaded machine: PR #23's Windows runners)
/// is debugged once it does, in the same start: netcoredbg gives up waiting, not the start. On macOS too, where
/// netcoredbg needs a request to take the runtime up (PR #24's CI).
/// </summary>
public sealed class LateRuntimeTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>
{
    [Fact]
    public async Task A_program_whose_runtime_starts_after_netcoredbgs_wait_stops_at_its_breakpoint_under_its_process_id()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The runtime's start is held back by a shell that sleeps, then execs dotnet in its own process (Unix).");
        var ct = TestContext.Current.CancellationToken;
        await using var debug = new DebugService(() => DebuggeeFixture.Adapter);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var output = new StringBuilder();
        debug.Output += (_, o) =>
        {
            lock (output)
            {
                output.Append(CultureInfo.InvariantCulture, $"[{clock.Elapsed.TotalSeconds:0.0}s] {o.Text}");
            }
        };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.StateChanged += (_, _) =>
        {
            if (debug.State == DebugState.Stopped)
            {
                stopped.TrySetResult();
            }
        };
        var depth = Path.Combine(debuggee.Folder, "Depth.cs");
        var loop = DebuggeeFixture.Line(depth, "depth-loop");
        debug.Breakpoints.Set(new BreakpointSpec(depth, loop));

        // The shell's process becomes the program's (exec): netcoredbg started it and waits for its runtime, which
        // starts 7 seconds later.
        // On macOS netcoredbg takes the late runtime up only after a request arrives (DebugAdapter.WaitForRuntimeAsync).
        try
        {
            await debug.LaunchAsync("/bin/sh", ["-c", "sleep 7; exec $0 $@", DebuggeeFixture.DotnetHost, "exec", debuggee.Dll, "--depth"], debuggee.Folder, new Dictionary<string, string>(), ct);
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Assert.Fail($"The program didn't stop ({ex.Message}). Debug Console:\n{Text()}");
        }

        Assert.Equal(loop, debug.SelectedFrame?.Line);
        var processId = (await debug.EvaluateAsync("System.Environment.ProcessId", "watch", ct)).Result;
        Assert.Equal(debug.ProcessId?.ToString(CultureInfo.InvariantCulture), processId);
        Assert.Equal(debug.ProcessId, debug.Processes.Single().Id);
        var text = Text();
        Assert.Contains("The debugger didn't see the program's runtime start within its 5 seconds; waiting for it", text, StringComparison.Ordinal);
        Assert.Contains("The debugger saw the program's runtime start ", text, StringComparison.Ordinal);

        // The same start, unless the runtime's debugger transport deadlocked at its start (a loaded Linux machine; the
        // .NET 11 runtime, DebugAdapter.WaitForRuntimeAsync): then the program was ended and started again.
        if (text.Contains("starting again", StringComparison.Ordinal))
        {
            Assert.Contains("The program's runtime started, but the debugger hasn't taken it up", text, StringComparison.Ordinal);
        }

        await debug.StopAsync();

        string Text()
        {
            lock (output)
            {
                return output.ToString();
            }
        }
    }

    [Fact]
    public async Task A_program_that_ends_before_its_runtime_starts_is_started_again_without_the_longer_wait()
    {
        // netcoredbg doesn't reap a program it never saw the runtime of: on Linux it stays a zombie, which
        // Process.HasExited reads as running, and the start waited out its 60 seconds instead of starting again.
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        Assert.SkipUnless(OperatingSystem.IsLinux(), "A program's state is read from /proc (DebugAdapter.HasExited). TODO(issue): macOS.");
        var ct = TestContext.Current.CancellationToken;
        await using var debug = new DebugService(() => DebuggeeFixture.Adapter);
        var output = new StringBuilder();
        debug.Output += (_, o) =>
        {
            lock (output)
            {
                output.Append(o.Text);
            }
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();

        // Each start's "program" ends a second after netcoredbg's 5 seconds, before any runtime started.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            debug.LaunchAsync("/bin/sh", ["-c", "sleep 6; exit 3"], debuggee.Folder, new Dictionary<string, string>(), ct).WaitAsync(TimeSpan.FromSeconds(150), ct));

        string text;
        lock (output)
        {
            text = output.ToString();
        }

        Assert.Contains("0x80004005", error.Message, StringComparison.Ordinal);
        Assert.Contains("before its runtime started", text, StringComparison.Ordinal);
        Assert.Contains($"starting again ({DebugService.StartupAttempts} of {DebugService.StartupAttempts})", text, StringComparison.Ordinal);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(60), $"took {clock.Elapsed}: a start waited out the longer wait. Debug Console:\n{text}");
    }
}
