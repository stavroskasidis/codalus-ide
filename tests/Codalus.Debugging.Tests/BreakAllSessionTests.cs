using System.Text;
using System.Threading.Channels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M58 (ADR 0094), several startup projects under the Codalus debugger: a stop in one program pauses the others, which
/// go on with it. Under the Codalus debugger whatever the shard (its adapter says it can,
/// <see cref="Capabilities.SupportsBreakWithOtherPrograms"/>); netcoredbg's programs keep running, which
/// <see cref="CompoundDebugAdapterTests"/> covers on fakes.
/// </summary>
public sealed class BreakAllSessionTests(SessionsFixture fixture) : IClassFixture<SessionsFixture>, IAsyncLifetime
{
    private readonly Channel<StopInfo> _stops = Channel.CreateUnbounded<StopInfo>();
    private readonly TaskCompletionSource<int?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _output = new();
    private DebugService _debug = null!;

    public ValueTask InitializeAsync()
    {
        // The Codalus debugger by the setting, with no netcoredbg to fall back on. Not in the parity list
        // (eng/ci-debug-parity.txt): a shard's CODALUS_DEBUG_ADAPTER would override the setting.
        _debug = new DebugService(() => null) { CoreClrAdapter = DebuggeeFixture.CodalusAdapter, AdapterSetting = () => DebugService.CodalusAdapter };
        _debug.Stopped += (_, stop) => _stops.Writer.TryWrite(stop);
        _debug.Ended += (_, code) => _ended.TrySetResult(code);
        _debug.Output += (_, o) =>
        {
            lock (_output)
            {
                _output.Append(o.Text);
            }
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_debug is not null)
        {
            await _debug.DisposeAsync();
        }
    }

    private string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    private LaunchPlan Plan(string project, params string[] arguments) =>
        new(DebuggeeFixture.DotnetHost, string.Empty, Path.GetDirectoryName(fixture.Project(project))!, new Dictionary<string, string>(), null) { ArgumentList = ["exec", fixture.Dll(project), .. arguments] };

    private async Task<StopInfo> NextStopAsync(int seconds = 90)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            return await _stops.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The program did not stop. State: {_debug.State}. Output:\n{Output}");
        }
    }

    [Fact]
    public async Task A_stop_in_one_program_pauses_the_other_and_they_go_on_together()
    {
        // M58, on by default: the first program waits for a file in a loop; the second stops at its breakpoint, and the
        // first is paused with it, where its frames can be read. Continue lets both go on: the first one finds the file
        // and reaches the same breakpoint, its own stop.
        var go = Path.Combine(Path.GetTempPath(), "codalus-go-" + Guid.NewGuid().ToString("N"));
        var debuggee = fixture.Source("Debuggee", "Program.cs");
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee, DebuggeeFixture.Line(debuggee, "done")));
        try
        {
            await _debug.LaunchAsync([new DebugLaunch("Waiter", Plan("Debuggee", "--wait-for", go)), new DebugLaunch("Debuggee", Plan("Debuggee"))], TestContext.Current.CancellationToken);
            await NextStopAsync();
            Assert.Equal(DebuggeeFixture.Line(debuggee, "done"), _debug.SelectedFrame?.Line);
            var stopped = _debug.SelectedThreadId;

            // The waiting program is paused in its loop: its main thread's frames are there to read.
            var waitLines = new[] { DebuggeeFixture.Line(debuggee, "wait-check"), DebuggeeFixture.Line(debuggee, "wait") };
            var deadline = DateTime.UtcNow.AddSeconds(60);
            var paused = false;
            var seen = string.Empty;
            while (!paused && DateTime.UtcNow < deadline)
            {
                try
                {
                    foreach (var (thread, frames) in await _debug.AllStacksAsync(20, TestContext.Current.CancellationToken))
                    {
                        paused |= thread != stopped && frames.Any(f => f.Source?.Path is { } path && path.EndsWith("Program.cs", StringComparison.Ordinal) && waitLines.Contains(f.Line));
                    }

                    seen = string.Join(", ", _debug.Threads.Select(t => t.Name));
                }
                catch (DapException ex)
                {
                    seen = ex.Message; // still running: its frames can't be read yet
                }

                if (!paused)
                {
                    await Task.Delay(100, TestContext.Current.CancellationToken);
                }
            }

            Assert.True(paused, $"The waiting program is paused in its loop. Threads: {seen}. Output:\n{Output}");
            Assert.Equal(DebugState.Stopped, _debug.State);

            // Both go on: the waiting program finds its file and stops at the breakpoint itself.
            File.WriteAllText(go, string.Empty);
            await _debug.ContinueAsync();
            await NextStopAsync();
            Assert.Equal(DebuggeeFixture.Line(debuggee, "done"), _debug.SelectedFrame?.Line);
            Assert.NotEqual(stopped, _debug.SelectedThreadId);
            await _debug.ContinueAsync();
            await _ended.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        }
        finally
        {
            File.Delete(go);
        }
    }
}
