namespace Codalus.Debugging.Tests;

/// <summary>
/// M58 (ADR 0094), on every OS: what the Codalus debugger does that netcoredbg left to the IDE or didn't do, against
/// the real runtime (<c>samples/Debugging/Debuggee --features</c>). A breakpoint's hit condition, message, filter and
/// dependency are applied by the adapter, so the program stops only where it breaks; Set Next Statement; Step Into
/// Specific; the value a method returned; frozen threads.
/// </summary>
public sealed class ClrDebuggerFeatureTests(DebuggeeFixture fixture) : IClassFixture<DebuggeeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DapJsonContext s_json = DapJsonContext.Default;

    private string Features => Path.Combine(fixture.Folder, "Features.cs");

    private int Line(string marker) => DebuggeeFixture.Line(Features, marker);

    private async Task<(DebugAdapter Adapter, EventLog Events)> StartAsync(params SourceBreakpoint[] breakpoints)
    {
        var adapter = await DebugAdapter.StartAsync(DebuggeeFixture.CodalusAdapter(), Ct);
        try
        {
            var events = new EventLog(adapter);
            await adapter.LaunchAsync(new NetFxDebugArguments(DebuggeeFixture.DotnetHost, ["exec", fixture.Dll, "--features"], fixture.Folder, Type: "coreclr"), s_json.NetFxDebugArguments, Ct);
            await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await adapter.SetBreakpointsAsync(Features, breakpoints, Ct);
            await adapter.ConfigurationDoneAsync(Ct);
            return (adapter, events);
        }
        catch
        {
            await adapter.DisposeAsync();
            throw;
        }
    }

    private static async Task<StackFrame> TopAsync(DebugAdapter adapter, StoppedEvent stop) =>
        (await adapter.StackTraceAsync(stop.ThreadId!.Value, 1, Ct)).StackFrames[0];

    [Fact]
    public async Task The_adapter_says_what_it_does_itself()
    {
        await using var adapter = await DebugAdapter.StartAsync(DebuggeeFixture.CodalusAdapter(), Ct);
        var capabilities = adapter.Capabilities;
        Assert.True(capabilities.SupportsHitConditionalBreakpoints);
        Assert.True(capabilities.SupportsLogPoints);
        Assert.True(capabilities.SupportsBreakpointFilters);
        Assert.True(capabilities.AppliesBreakpointBehaviors);
        Assert.True(((IDebugAdapter)adapter).AppliesBreakpointBehaviors(1));
        Assert.True(capabilities.SupportsGotoTargetsRequest);
        Assert.True(capabilities.SupportsStepInTargetsRequest);
        Assert.True(capabilities.SupportsFreezingThreads);
    }

    [Fact]
    public async Task Hit_counts_logpoints_filters_and_dependencies_stop_the_program_only_where_they_break()
    {
        var (adapter, events) = await StartAsync(
            new SourceBreakpoint(Line("features-loop"), HitCondition: "== 3"),
            // A logpoint: written, never a stop; and it counts as hit for the breakpoint that depends on it.
            new SourceBreakpoint(Line("features-measure"), LogMessage: "measuring {text} after {count} in $FUNCTION on {{$TID}}"),
            // Depends on a breakpoint that is never hit: never breaks.
            new SourceBreakpoint(Line("features-print"), DependsOn: new BreakpointDependency(Features, Line("features-end"))),
            // One line, two threads: only the one the filter names breaks.
            new SourceBreakpoint(Line("features-mark"), Filter: "ThreadName = \"Tagger B\""),
            // Depends on the logpoint.
            new SourceBreakpoint(Line("features-freeze"), DependsOn: new BreakpointDependency(Features, Line("features-measure"))));
        await using var _ = adapter;

        // The third time through the loop, and only then: i is 2, and the first two rounds ran.
        var third = await events.StoppedAsync();
        Assert.Equal("breakpoint", third.Reason);
        var top = await TopAsync(adapter, third);
        Assert.Equal(Line("features-loop"), top.Line);
        Assert.Equal("2", (await adapter.EvaluateAsync("i", top.Id, "watch", Ct)).Result);
        Assert.Equal("1", (await adapter.EvaluateAsync("count", top.Id, "watch", Ct)).Result);

        // The same breakpoint with another condition counts from now: every second hit is the one with i = 4.
        var again = await adapter.SetBreakpointsAsync(Features, [
            new SourceBreakpoint(Line("features-loop"), HitCondition: "% 2"),
            new SourceBreakpoint(Line("features-measure"), LogMessage: "measuring {text} after {count} in $FUNCTION on {{$TID}}"),
            new SourceBreakpoint(Line("features-print"), DependsOn: new BreakpointDependency(Features, Line("features-end"))),
            new SourceBreakpoint(Line("features-mark"), Filter: "ThreadName = \"Tagger B\""),
            new SourceBreakpoint(Line("features-freeze"), DependsOn: new BreakpointDependency(Features, Line("features-measure"))),
        ], Ct);
        Assert.All(again, b => Assert.True(b.Verified));
        await adapter.ContinueAsync(third.ThreadId!.Value, Ct);
        var every = await events.StoppedAsync();
        top = await TopAsync(adapter, every);
        Assert.Equal(Line("features-loop"), top.Line);
        Assert.Equal("4", (await adapter.EvaluateAsync("i", top.Id, "watch", Ct)).Result);

        // On: past the logpoint and the breakpoint whose dependency is never hit, to the filtered one, on its thread.
        await adapter.ContinueAsync(every.ThreadId!.Value, Ct);
        var filtered = await events.StoppedAsync();
        top = await TopAsync(adapter, filtered);
        Assert.Equal(Line("features-mark"), top.Line);
        Assert.Equal("\"Tagger B\"", (await adapter.EvaluateAsync("name", top.Id, "watch", Ct)).Result);
        Assert.Equal("Tagger B", (await adapter.ThreadsAsync(Ct)).Single(t => t.Id == filtered.ThreadId).Name);
        Assert.Contains($"measuring \"A-BB!\" after 15 in Debuggee.Features.Run on {{{third.ThreadId}}}", events.Output, StringComparison.Ordinal);
        Assert.Contains("<Tagger A>", events.Output, StringComparison.Ordinal);

        // And the one that depends on the logpoint, which was hit.
        await adapter.ContinueAsync(filtered.ThreadId!.Value, Ct);
        var dependent = await events.StoppedAsync();
        Assert.Equal(Line("features-freeze"), (await TopAsync(adapter, dependent)).Line);

        // Four stops and no more: nothing stopped the program for a hit that didn't break.
        Assert.Equal(4, events.All.Count(e => e.Name == DapEvents.Stopped));
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Fact]
    public async Task Set_next_statement_moves_the_thread_within_its_method_and_says_why_it_cannot_elsewhere()
    {
        var (adapter, events) = await StartAsync(new SourceBreakpoint(Line("features-print")));
        await using var _ = adapter;
        var stop = await events.StoppedAsync();
        var thread = stop.ThreadId!.Value;
        var top = await TopAsync(adapter, stop);
        Assert.Equal(Line("features-print"), top.Line);
        Assert.Equal("15", (await adapter.EvaluateAsync("count", top.Id, "watch", Ct)).Result);

        // Back into the loop's body: the thread is there, and nothing ran on the way.
        var target = Assert.Single(await adapter.GotoTargetsAsync(thread, Features, Line("features-loop"), Ct));
        Assert.Equal(Line("features-loop"), target.Line);
        await adapter.GotoAsync(thread, target.Id, Ct);
        var moved = await events.StoppedAsync();
        Assert.Equal("goto", moved.Reason);
        Assert.Equal(thread, moved.ThreadId);
        top = await TopAsync(adapter, moved);
        Assert.Equal(Line("features-loop"), top.Line);
        Assert.Equal("15", (await adapter.EvaluateAsync("count", top.Id, "watch", Ct)).Result);

        // The line runs again from there: i was 6 when the loop ended, so count becomes 21.
        await adapter.NextAsync(thread, Ct);
        var stepped = await events.StoppedAsync();
        Assert.Equal("step", stepped.Reason);
        top = await TopAsync(adapter, stepped);
        Assert.Equal("21", (await adapter.EvaluateAsync("count", top.Id, "watch", Ct)).Result);

        // A line of another method: refused, with the reason, and the thread stays where it is.
        var elsewhere = Assert.Single(await adapter.GotoTargetsAsync(thread, Features, Line("features-measure-body"), Ct));
        var refused = await Assert.ThrowsAsync<DapException>(() => adapter.GotoAsync(thread, elsewhere.Id, Ct));
        Assert.Contains("a line of the method the thread is in", refused.Message, StringComparison.Ordinal);
        Assert.Equal(top.Line, (await adapter.StackTraceAsync(thread, 1, Ct)).StackFrames[0].Line);

        // A line with no code anywhere: no target.
        Assert.Empty(await adapter.GotoTargetsAsync(thread, Features, 1, Ct));
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Fact]
    public async Task Step_into_specific_lists_the_lines_calls_and_steps_into_the_chosen_one()
    {
        var (adapter, events) = await StartAsync(new SourceBreakpoint(Line("features-nested")));
        await using var _ = adapter;
        var stop = await events.StoppedAsync();
        var thread = stop.ThreadId!.Value;
        var top = await TopAsync(adapter, stop);

        // The line's calls, innermost first (the order they run in).
        var targets = await adapter.StepInTargetsAsync(top.Id, Ct);
        Assert.Equal(["Debuggee.Features.Twice", "Debuggee.Features.Join", "Debuggee.Features.Shout"], targets.Select(t => t.Label));

        // Into Join: Twice runs on the way without a stop, and its result is Join's argument.
        await adapter.StepInAsync(thread, targets[1].Id, Ct);
        var inJoin = await events.StoppedAsync();
        Assert.Equal("step", inJoin.Reason);
        top = await TopAsync(adapter, inJoin);
        Assert.Equal("Debuggee.Features.Join", top.Name);
        Assert.Equal("\"bb\"", (await adapter.EvaluateAsync("right", top.Id, "watch", Ct)).Result);

        // Out again: back on the line, with what Join returned first among the locals.
        await adapter.StepOutAsync(thread, Ct);
        var back = await events.StoppedAsync();
        top = await TopAsync(adapter, back);
        Assert.Equal(Line("features-nested"), top.Line);
        var locals = await adapter.VariablesAsync(Assert.Single(await adapter.ScopesAsync(top.Id, Ct)).VariablesReference, Ct);
        Assert.Equal("Debuggee.Features.Join returned", locals[0].Name);
        Assert.Equal("\"a-bb\"", locals[0].Value);
        Assert.Equal("string", locals[0].Type);

        // What is left of the line: Shout only.
        targets = await adapter.StepInTargetsAsync(top.Id, Ct);
        Assert.Equal(["Debuggee.Features.Shout"], targets.Select(t => t.Label));

        // The returned value is the last step's: gone after the next one.
        await adapter.NextAsync(thread, Ct);
        var next = await events.StoppedAsync();
        top = await TopAsync(adapter, next);
        Assert.Equal(Line("features-measure"), top.Line);
        locals = await adapter.VariablesAsync(Assert.Single(await adapter.ScopesAsync(top.Id, Ct)).VariablesReference, Ct);
        Assert.DoesNotContain(locals, v => v.Name.EndsWith(" returned", StringComparison.Ordinal));

        // An int, from a method stepped into and out of.
        await adapter.StepInAsync(thread, Ct);
        Assert.Equal("Debuggee.Features.Measure", (await TopAsync(adapter, await events.StoppedAsync())).Name);
        await adapter.StepOutAsync(thread, Ct);
        top = await TopAsync(adapter, await events.StoppedAsync());
        locals = await adapter.VariablesAsync(Assert.Single(await adapter.ScopesAsync(top.Id, Ct)).VariablesReference, Ct);
        Assert.Equal("Debuggee.Features.Measure returned", locals[0].Name);
        Assert.Equal("5", locals[0].Value);
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Fact]
    public async Task A_frozen_thread_does_not_run_until_it_is_thawed()
    {
        var (adapter, events) = await StartAsync(new SourceBreakpoint(Line("features-freeze")));
        await using var _ = adapter;
        var stop = await events.StoppedAsync();
        var thread = stop.ThreadId!.Value;
        var top = await TopAsync(adapter, stop);
        var ticker = (await adapter.ThreadsAsync(Ct)).Single(t => t.Name == "Ticker");
        Assert.Null(ticker.Frozen);

        // Frozen: the program's other threads run (the step's sleep), and its counter stands still.
        await adapter.FreezeThreadAsync(ticker.Id, frozen: true, Ct);
        Assert.True((await adapter.ThreadsAsync(Ct)).Single(t => t.Id == ticker.Id).Frozen);
        var before = (await adapter.EvaluateAsync("s_ticks", top.Id, "watch", Ct)).Result;
        await adapter.NextAsync(thread, Ct);
        top = await TopAsync(adapter, await events.StoppedAsync());
        Assert.Equal(Line("features-frozen"), top.Line);
        Assert.Equal(before, (await adapter.EvaluateAsync("s_ticks", top.Id, "watch", Ct)).Result);

        // A func-eval lets every thread run while it runs: the frozen one is frozen again after it.
        Assert.Equal("\"x\"", (await adapter.EvaluateAsync("Features.Twice(\"\") + \"x\"", top.Id, "watch", Ct)).Result);
        await adapter.NextAsync(thread, Ct);
        top = await TopAsync(adapter, await events.StoppedAsync());
        Assert.Equal(Line("features-thawed"), top.Line);
        Assert.Equal(before, (await adapter.EvaluateAsync("s_ticks", top.Id, "watch", Ct)).Result);

        // Thawed: it runs again.
        await adapter.FreezeThreadAsync(ticker.Id, frozen: false, Ct);
        Assert.Null((await adapter.ThreadsAsync(Ct)).Single(t => t.Id == ticker.Id).Frozen);
        await adapter.NextAsync(thread, Ct);
        top = await TopAsync(adapter, await events.StoppedAsync());
        Assert.NotEqual(before, (await adapter.EvaluateAsync("s_ticks", top.Id, "watch", Ct)).Result);

        // And the program ends by itself: the thread it joins wasn't left frozen.
        await adapter.ContinueAsync(thread, Ct);
        Assert.Equal(15, (await events.NextAsync(DapEvents.Exited)).Body.GetProperty("exitCode").GetInt32());
    }
}
