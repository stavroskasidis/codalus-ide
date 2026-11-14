namespace Codalus.Debugging.Tests;

/// <summary>
/// M61b (ADR 0100): the async call stack of the Codalus debugger. Stopped in an async method that resumed after an
/// await, the thread's stack ends in the thread pool; below it the Call Stack shows the calls awaiting the method, read
/// from the tasks' continuations, each at its await's line and with its state machine's variables. State machines
/// only: runtime async's awaiting calls aren't exposed by the runtime, so its frames end as before (a limit).
/// <c>samples/Debugging/Async.slnx</c>'s chain: Main awaits OuterAsync, which awaits MiddleAsync, which awaits InnerAsync.
/// </summary>
public sealed class AsyncCallStackTests(AsyncDebuggeeFixture fixture) : IClassFixture<AsyncDebuggeeFixture>
{
    private const string Async = "[Async] ";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DapJsonContext s_json = DapJsonContext.Default;

    public static TheoryData<string> Builds => new(AsyncDebuggeeFixture.StateMachines, AsyncDebuggeeFixture.RuntimeAsync);

    private string Chain => fixture.Source("Chain.cs");

    private string Program => fixture.Source("Program.cs");

    private async Task<(DebugAdapter Adapter, EventLog Events, StoppedEvent Stop)> StopInInnerAsync(string build)
    {
        var (dll, folder) = fixture.Program(build);
        var adapter = await DebugAdapter.StartAsync(DebuggeeFixture.CodalusAdapter(), Ct);
        try
        {
            var events = new EventLog(adapter);
            await adapter.LaunchAsync(new NetFxDebugArguments(DebuggeeFixture.DotnetHost, ["exec", dll, "chain"], folder, Type: "coreclr"), s_json.NetFxDebugArguments, Ct);
            await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await adapter.SetBreakpointsAsync(Chain, [new SourceBreakpoint(DebuggeeFixture.Line(Chain, "chain-inner"))], Ct);
            await adapter.ConfigurationDoneAsync(Ct);
            var stop = await events.StoppedAsync();
            Assert.Equal("breakpoint", stop.Reason);
            return (adapter, events, stop);
        }
        catch
        {
            await adapter.DisposeAsync();
            throw;
        }
    }

    private static string Shown(IEnumerable<StackFrame> frames) => string.Join("\n", frames.Select(f => $"{f.Name} {f.Source?.Name} {f.Line} {f.PresentationHint}"));

    /// <summary>The frames of the async call stack: the last ones, after the thread's own.</summary>
    private static List<StackFrame> AwaitingCallers(StackFrame[] frames)
    {
        var callers = frames.SkipWhile(f => !f.Name.StartsWith(Async, StringComparison.Ordinal)).ToList();
        Assert.True(callers.All(f => f.Name.StartsWith(Async, StringComparison.Ordinal)), "The async call stack isn't below the thread's own frames:\n" + Shown(frames));
        return callers;
    }

    private static async Task<Dictionary<string, string>> LocalsAsync(DebugAdapter adapter, StackFrame frame) =>
        (await adapter.VariablesAsync(Assert.Single(await adapter.ScopesAsync(frame.Id, Ct)).VariablesReference, Ct)).ToDictionary(v => v.Name, v => v.Value);

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task A_stop_after_an_await_deep_in_a_chain_shows_the_awaiting_callers_with_their_lines(string build)
    {
        var (adapter, _, stop) = await StopInInnerAsync(build);
        await using var session = adapter;
        var trace = await adapter.StackTraceAsync(stop.ThreadId!.Value, 0, Ct);
        var frames = trace.StackFrames;
        var shown = Shown(frames);
        Assert.Contains("InnerAsync", frames[0].Name, StringComparison.Ordinal);
        Assert.Equal(DebuggeeFixture.Line(Chain, "chain-inner"), frames[0].Line);
        var callers = AwaitingCallers(frames);
        if (build == AsyncDebuggeeFixture.StateMachines)
        {
            // Each awaiting call by its method's name, at the await it waits in, in the order they await: the user's
            // code, so not subtle; after the thread pool's frames, which end the thread's stack.
            Assert.True(callers.Count == 3, shown);
            Assert.Equal(["[Async] AsyncDebuggee.Chain.MiddleAsync", "[Async] AsyncDebuggee.Chain.OuterAsync", "[Async] AsyncDebuggee.Program.Main"], callers.Select(f => f.Name));
            Assert.Equal([Chain, Chain, Program], callers.Select(f => f.Source?.Path));
            Assert.Equal([DebuggeeFixture.Line(Chain, "chain-middle"), DebuggeeFixture.Line(Chain, "chain-outer"), DebuggeeFixture.Line(Program, "main-chain")], callers.Select(f => f.Line));
            Assert.All(callers, f => Assert.Null(f.PresentationHint));
            Assert.True(frames[^(callers.Count + 1)].PresentationHint is "subtle" or "label", shown);
            Assert.Equal(frames.Length, trace.TotalFrames);

            // The Threads window asks for the top frame alone: the awaiting callers aren't read for it.
            var top = await adapter.StackTraceAsync(stop.ThreadId!.Value, 1, Ct);
            Assert.Equal(frames[0].Name, Assert.Single(top.StackFrames).Name);
        }
        else
        {
            // A limit (ADR 0100): the runtime doesn't expose which call awaits a runtime-async call, so the frames end
            // in its dispatcher as before, with the resumed method the only one of the user's.
            Assert.True(callers.Count == 0, shown);
            Assert.True(frames.Count(f => f.PresentationHint is null) == 1, shown);
        }

        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Fact]
    public async Task A_caller_that_doesnt_have_the_methods_task_yet_is_found_on_its_threads_stack()
    {
        // The method resumed on another thread while its caller's thread is still inside its await (the program's own
        // race, seen under CPU load in this class's chain): its task names no awaiting call yet. The caller, and Main
        // below it, are running on that thread's stack, not suspended: each is shown where it runs there.
        var elsewhere = fixture.Source("Elsewhere.cs");
        var (dll, folder) = fixture.Program(AsyncDebuggeeFixture.StateMachines);
        await using var adapter = await DebugAdapter.StartAsync(DebuggeeFixture.CodalusAdapter(), Ct);
        var events = new EventLog(adapter);
        await adapter.LaunchAsync(new NetFxDebugArguments(DebuggeeFixture.DotnetHost, ["exec", dll, "held"], folder, Type: "coreclr"), s_json.NetFxDebugArguments, Ct);
        await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await adapter.SetBreakpointsAsync(elsewhere, [new SourceBreakpoint(DebuggeeFixture.Line(elsewhere, "held-resumed"))], Ct);
        await adapter.ConfigurationDoneAsync(Ct);
        var stop = await events.StoppedAsync();
        var frames = (await adapter.StackTraceAsync(stop.ThreadId!.Value, 0, Ct)).StackFrames;
        var callers = AwaitingCallers(frames);
        Assert.True(callers.Count == 2, Shown(frames));
        Assert.Equal(["[Async] AsyncDebuggee.Held.HoldAsync", "[Async] AsyncDebuggee.Program.Main"], callers.Select(f => f.Name));
        Assert.Equal([DebuggeeFixture.Line(elsewhere, "held-call"), DebuggeeFixture.Line(Program, "main-held")], callers.Select(f => f.Line));
        Assert.Equal("3", (await LocalsAsync(adapter, callers[0]))["value"]);
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Fact]
    public async Task An_awaiting_caller_shows_its_variables_and_evaluates_in_its_scope()
    {
        var (adapter, events, stop) = await StopInInnerAsync(AsyncDebuggeeFixture.StateMachines);
        await using var session = adapter;
        var callers = AwaitingCallers((await adapter.StackTraceAsync(stop.ThreadId!.Value, 0, Ct)).StackFrames);
        Assert.Equal(3, callers.Count);

        // Its state machine's fields: its parameters, and its variables (one not assigned yet, waiting for the await).
        var middle = await LocalsAsync(adapter, callers[0]);
        Assert.Equal("6", middle["seed"]);
        Assert.Equal("0", middle["inner"]);
        Assert.DoesNotContain(middle.Keys, n => n.StartsWith('<'));
        Assert.Equal("12", (await adapter.EvaluateAsync("seed * 2", callers[0].Id, "watch", Ct)).Result);
        Assert.Equal("5", (await LocalsAsync(adapter, callers[1]))["seed"]);
        var main = await LocalsAsync(adapter, callers[2]);
        Assert.Equal("\"chain\"", main["scenario"]);
        Assert.Equal("0", main["total"]);
        Assert.Equal("\"chain\"", (await adapter.EvaluateAsync("scenario", callers[2].Id, "watch", Ct)).Result);

        // After a step, the callers are read anew (what held them was let go), the same.
        await adapter.NextAsync(stop.ThreadId!.Value, Ct);
        stop = await events.StoppedAsync(30);
        var frames = (await adapter.StackTraceAsync(stop.ThreadId!.Value, 0, Ct)).StackFrames;
        Assert.Equal(DebuggeeFixture.Line(Chain, "chain-inner-return"), frames[0].Line);
        Assert.Equal("112", (await adapter.EvaluateAsync("deepest", frames[0].Id, "watch", Ct)).Result);
        callers = AwaitingCallers(frames);
        Assert.Equal([DebuggeeFixture.Line(Chain, "chain-middle"), DebuggeeFixture.Line(Chain, "chain-outer"), DebuggeeFixture.Line(Program, "main-chain")], callers.Select(f => f.Line));

        // And the program runs on to its end as without them.
        await adapter.ContinueAsync(stop.ThreadId!.Value, Ct);
        await events.NextAsync(DapEvents.Terminated);
        await events.OutputContainsAsync("async total=113");
    }
}
