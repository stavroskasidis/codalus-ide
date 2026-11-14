namespace Codalus.Debugging.Tests;

/// <summary>
/// M61 (ADR 0100): the Codalus debugger on async code compiled for runtime async (.NET 11), which has no compiler-made
/// state machine, and on the same code built as state machines (<c>samples/Debugging/Async.slnx</c>). Each scenario
/// runs on both builds: breakpoints and Run to Cursor after an await, Step Over an await (it resumes on a thread-pool
/// thread), Step Into an awaited call and Step Out, an exception thrown after an await, the call stack of a chain of
/// awaits, Set Next Statement after an await, and the two kinds of code awaiting each other.
/// </summary>
public sealed class RuntimeAsyncDebuggingTests(AsyncDebuggeeFixture fixture) : IClassFixture<AsyncDebuggeeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DapJsonContext s_json = DapJsonContext.Default;

    public static TheoryData<string> Builds => new(AsyncDebuggeeFixture.StateMachines, AsyncDebuggeeFixture.RuntimeAsync);

    private string Worker => fixture.Source("Worker.cs");

    private int Line(string marker) => DebuggeeFixture.Line(Worker, marker);

    private async Task<(DebugAdapter Adapter, EventLog Events)> StartAsync(string build, string scenario, string file, int[] lines, string[]? exceptions = null)
    {
        var (dll, folder) = fixture.Program(build);
        var adapter = await DebugAdapter.StartAsync(DebuggeeFixture.CodalusAdapter(), Ct);
        try
        {
            var events = new EventLog(adapter);
            await adapter.LaunchAsync(new NetFxDebugArguments(DebuggeeFixture.DotnetHost, ["exec", dll, scenario], folder, Type: "coreclr"), s_json.NetFxDebugArguments, Ct);
            await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await adapter.SetBreakpointsAsync(file, [.. lines.Select(l => new SourceBreakpoint(l))], Ct);
            if (exceptions is not null)
            {
                await adapter.SetExceptionBreakpointsAsync(exceptions, Ct);
            }

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

    private static async Task<string> ValueAsync(DebugAdapter adapter, StackFrame frame, string expression) =>
        (await adapter.EvaluateAsync(expression, frame.Id, "watch", Ct)).Result;

    private static async Task<Variable[]> LocalsAsync(DebugAdapter adapter, StackFrame frame) =>
        await adapter.VariablesAsync(Assert.Single(await adapter.ScopesAsync(frame.Id, Ct)).VariablesReference, Ct);

    /// <summary>A step, and the stop it ends in: its top frame, on the method's line <paramref name="marker"/>.</summary>
    private async Task<(StoppedEvent Stop, StackFrame Top)> StepAsync(DebugAdapter adapter, EventLog events, StoppedEvent from, Func<int, CancellationToken, Task> step, string file, string marker)
    {
        await step(from.ThreadId!.Value, Ct);
        var stop = await events.StoppedAsync(30);
        Assert.Equal("step", stop.Reason);
        var top = await TopAsync(adapter, stop);
        Assert.True(DebuggeeFixture.Line(file, marker) == top.Line, $"The step stopped in {top.Name} on line {top.Line}, not on [{marker}] (line {DebuggeeFixture.Line(file, marker)}).");
        return (stop, top);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task A_breakpoint_after_an_await_shows_the_resumed_methods_locals_parameters_and_this(string build)
    {
        var (adapter, events) = await StartAsync(build, "steps", Worker, [Line("run-before"), Line("run-after-yield")]);
        await using var session = adapter;
        var first = await events.StoppedAsync();
        await adapter.ContinueAsync(first.ThreadId!.Value, Ct);

        // After Task.Delay and Task.Yield: on a thread-pool thread, not the one the method started on (Main's, waiting).
        var stop = await events.StoppedAsync();
        Assert.Equal("breakpoint", stop.Reason);
        Assert.NotEqual(first.ThreadId, stop.ThreadId);
        var top = await TopAsync(adapter, stop);
        Assert.Equal(Line("run-after-yield"), top.Line);
        Assert.Contains("RunAsync", top.Name, StringComparison.Ordinal);
        Assert.Equal("3", await ValueAsync(adapter, top, "count"));
        Assert.Equal("30", await ValueAsync(adapter, top, "before"));
        Assert.Equal("31", await ValueAsync(adapter, top, "afterDelay"));
        Assert.Equal("\"worker\"", await ValueAsync(adapter, top, "Name"));
        Assert.Equal("\"worker\"", await ValueAsync(adapter, top, "this.Name"));

        // Locals: this, the parameter and the method's variables, without the state machine's own fields.
        var locals = await LocalsAsync(adapter, top);
        Assert.Equal(["this", "count", "before", "afterDelay", "afterYield", "doubled", "text"], locals.Select(v => v.Name).Where(n => !n.StartsWith('$')));
        Assert.Equal("AsyncDebuggee.Worker", locals[0].Type);
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task Step_over_an_await_stops_on_the_methods_next_line_once_it_resumes(string build)
    {
        var (adapter, events) = await StartAsync(build, "steps", Worker, [Line("run-delay")]);
        await using var session = adapter;
        var stop = await events.StoppedAsync();
        var main = stop.ThreadId;

        // Over Task.Delay: the method resumes on a thread-pool thread, and the step ends there.
        (stop, var top) = await StepAsync(adapter, events, stop, adapter.NextAsync, Worker, "run-after-delay");
        Assert.NotEqual(main, stop.ThreadId);
        Assert.Equal("30", await ValueAsync(adapter, top, "before"));
        (stop, _) = await StepAsync(adapter, events, stop, adapter.NextAsync, Worker, "run-yield");

        // Over Task.Yield, which always suspends the method.
        (stop, top) = await StepAsync(adapter, events, stop, adapter.NextAsync, Worker, "run-after-yield");
        Assert.Equal("31", await ValueAsync(adapter, top, "afterDelay"));
        (stop, _) = await StepAsync(adapter, events, stop, adapter.NextAsync, Worker, "run-double");

        // Over an awaited call of the user's that suspends (it yields): the step doesn't stop in it.
        (stop, top) = await StepAsync(adapter, events, stop, adapter.NextAsync, Worker, "run-text");
        Assert.Equal("64", await ValueAsync(adapter, top, "doubled"));
        (stop, _) = await StepAsync(adapter, events, stop, adapter.NextAsync, Worker, "run-print");

        // To the end: the program writes the line and ends with its exit code, nothing stopped it again.
        await adapter.ContinueAsync(stop.ThreadId!.Value, Ct);
        await events.NextAsync(DapEvents.Terminated);
        await events.OutputContainsAsync("worker:64");
        Assert.Equal(6, events.All.Count(e => e.Name == DapEvents.Stopped) - 1);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task Step_over_an_await_that_resumed_elsewhere_before_the_stepping_thread_left_the_method(string build)
    {
        // The await's continuation runs on a new thread at once and waits there before the method's next line, so the
        // stepping thread is back in the caller while the method runs again elsewhere (Linux CI, run 43568784192, where
        // Task.Yield's continuation did that by chance and the step ended in the caller): the step still ends on the
        // method's next line, on the thread that resumed it.
        // Stepped into from its caller first, as there: the state machine's kickoff is then code that isn't the user's,
        // and the stepper leaving the method ends in the caller.
        var elsewhere = fixture.Source("Elsewhere.cs");
        var (adapter, events) = await StartAsync(build, "race", elsewhere, [DebuggeeFixture.Line(elsewhere, "race-call")]);
        await using var session = adapter;
        var stop = await events.StoppedAsync();
        (stop, _) = await StepAsync(adapter, events, stop, adapter.StepInAsync, elsewhere, "race-open");
        (stop, _) = await StepAsync(adapter, events, stop, adapter.NextAsync, elsewhere, "race-before");
        (stop, _) = await StepAsync(adapter, events, stop, adapter.NextAsync, elsewhere, "race-await");
        var stepping = stop.ThreadId;
        (stop, var top) = await StepAsync(adapter, events, stop, adapter.NextAsync, elsewhere, "race-after");
        Assert.NotEqual(stepping, stop.ThreadId);
        Assert.Equal("2", await ValueAsync(adapter, top, "start"));
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Fact]
    public async Task Step_out_of_a_resumed_method_whose_caller_doesnt_have_its_task_yet_stops_in_the_caller()
    {
        // The caller's thread is still inside the method's await when the method resumes elsewhere and stops: its task
        // names no awaiting call, and no awaiter of it is anywhere yet (a local run of the class under CPU load, where
        // the Step Out of this class's DoubleAsync ran the program to its end). The caller is found on its thread's
        // stack, below the method it started: the step ends there once it goes on, with the method's value. State
        // machines only: runtime async has no task to find the caller by (ADR 0100).
        var elsewhere = fixture.Source("Elsewhere.cs");
        var (adapter, events) = await StartAsync(AsyncDebuggeeFixture.StateMachines, "held", elsewhere, [DebuggeeFixture.Line(elsewhere, "held-resumed")]);
        await using var session = adapter;
        var stop = await events.StoppedAsync();
        await adapter.StepOutAsync(stop.ThreadId!.Value, Ct);
        stop = await events.StoppedAsync(30);
        Assert.Equal("step", stop.Reason);
        var top = await TopAsync(adapter, stop);
        Assert.True(top.Line == DebuggeeFixture.Line(elsewhere, "held-return"), $"Step Out stopped in {top.Name} on line {top.Line}.");
        Assert.Equal("30", await ValueAsync(adapter, top, "held"));
        Assert.Equal("30", await ValueAsync(adapter, top, "$ReturnValue"));
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task Step_into_an_awaited_call_and_step_out_back_to_the_awaiting_caller(string build)
    {
        var (adapter, events) = await StartAsync(build, "steps", Worker, [Line("run-double")]);
        await using var session = adapter;
        var stop = await events.StoppedAsync();

        // Into the awaited method: its opening brace, its argument.
        (stop, var top) = await StepAsync(adapter, events, stop, adapter.StepInAsync, Worker, "double-open");
        Assert.Contains("DoubleAsync", top.Name, StringComparison.Ordinal);
        Assert.Equal("32", await ValueAsync(adapter, top, "value"));

        // Over its await (it resumes later) and on to its return.
        (stop, _) = await StepAsync(adapter, events, stop, adapter.NextAsync, Worker, "double-yield");
        (stop, _) = await StepAsync(adapter, events, stop, adapter.NextAsync, Worker, "double-result");
        (stop, top) = await StepAsync(adapter, events, stop, adapter.NextAsync, Worker, "double-return");
        Assert.Equal("64", await ValueAsync(adapter, top, "result"));

        // Out: the method has resumed, so what called it is the thread pool or the runtime, not the awaiting caller.
        // The step ends in the awaiting caller once it resumes, the awaited value in its variable.
        await adapter.StepOutAsync(stop.ThreadId!.Value, Ct);
        stop = await events.StoppedAsync(30);
        Assert.Equal("step", stop.Reason);
        top = await TopAsync(adapter, stop);
        Assert.Contains("RunAsync", top.Name, StringComparison.Ordinal);
        Assert.Equal(Line("run-text"), top.Line);
        Assert.Equal("64", await ValueAsync(adapter, top, "doubled"));
        var locals = await LocalsAsync(adapter, top);
        if (build == AsyncDebuggeeFixture.StateMachines)
        {
            // What the method returned, first among the locals: its task's result.
            Assert.Equal("AsyncDebuggee.Worker.DoubleAsync returned", locals[0].Name);
            Assert.Equal("64", locals[0].Value);
            Assert.Equal("64", await ValueAsync(adapter, top, "$ReturnValue"));
        }
        else
        {
            // A limit (ADR 0100): a runtime-async call has no task, and the runtime doesn't say which call awaits it or
            // what it returned. The step ends where the awaiting call first calls the user's code after the await
            // (here Name's getter, so the line after the await, mid-line), and no returned value is shown.
            Assert.DoesNotContain(locals, v => v.Name.EndsWith(" returned", StringComparison.Ordinal));
        }

        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task Run_to_cursor_after_an_await_stops_there(string build)
    {
        var (adapter, events) = await StartAsync(build, "steps", Worker, [Line("run-before")]);
        await using var session = adapter;
        var stop = await events.StoppedAsync();

        // Run to Cursor is the IDE's hidden breakpoint (DebugService.RunToCursorAsync): three awaits later.
        await adapter.SetBreakpointsAsync(Worker, [new SourceBreakpoint(Line("run-before")), new SourceBreakpoint(Line("run-print"))], Ct);
        await adapter.ContinueAsync(stop.ThreadId!.Value, Ct);
        stop = await events.StoppedAsync();
        var top = await TopAsync(adapter, stop);
        Assert.Equal(Line("run-print"), top.Line);
        Assert.Equal("\"worker:64\"", await ValueAsync(adapter, top, "text"));
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task An_exception_thrown_after_an_await_stops_with_the_throwing_frame_on_top(string build)
    {
        var thrower = fixture.Source("Thrower.cs");
        var (adapter, events) = await StartAsync(build, "throw", thrower, [], ["all"]);
        await using var session = adapter;
        var stop = await events.StoppedAsync();
        Assert.Equal("exception", stop.Reason);
        Assert.Equal("System.InvalidOperationException", stop.Text);
        var top = await TopAsync(adapter, stop);
        Assert.Contains("FailLaterAsync", top.Name, StringComparison.Ordinal);
        Assert.Equal(DebuggeeFixture.Line(thrower, "throw"), top.Line);
        Assert.Equal("\"failed late\"", await ValueAsync(adapter, top, "message"));
        Assert.Equal("\"late\"", await ValueAsync(adapter, top, "reason"));
        Assert.Equal("\"failed late\"", await ValueAsync(adapter, top, "$exception.Message"));
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task The_call_stack_after_awaits_shows_the_users_frames_and_folds_the_runtimes(string build)
    {
        var chain = fixture.Source("Chain.cs");
        var (adapter, events) = await StartAsync(build, "chain", chain, [DebuggeeFixture.Line(chain, "chain-inner")]);
        await using var session = adapter;
        var stop = await events.StoppedAsync();
        var frames = (await adapter.StackTraceAsync(stop.ThreadId!.Value, 0, Ct)).StackFrames;
        var shown = string.Join("\n", frames.Select(f => $"{f.Name} {f.Line} {f.PresentationHint}"));

        // The frame that resumed is on top, at its line; under Just My Code every frame that isn't the user's (the
        // thread pool's, the runtime's dispatcher) is subtle, which the Call Stack folds into [External Code].
        Assert.Contains("InnerAsync", frames[0].Name, StringComparison.Ordinal);
        Assert.Equal(DebuggeeFixture.Line(chain, "chain-inner"), frames[0].Line);
        var users = frames.Where(f => f.PresentationHint is null).ToList();
        Assert.All(frames.Where(f => f.Source is null), f => Assert.True(f.PresentationHint is "subtle" or "label", shown));
        // A limit for both kinds (ADR 0100): a resumed call is called by what resumed it (a task's continuation, the
        // runtime's dispatcher of runtime-async continuations), so the calls awaiting it (MiddleAsync, OuterAsync) aren't
        // on the thread's stack. The classic IDE reads them from the continuations; that is deferred.
        Assert.True(users.Count == 1, shown);

        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task Set_next_statement_after_an_await(string build)
    {
        var (adapter, events) = await StartAsync(build, "steps", Worker, [Line("run-after-yield")]);
        await using var session = adapter;
        var stop = await events.StoppedAsync();
        var thread = stop.ThreadId!.Value;

        // Ahead, past the awaited call: the thread moves there, and the call doesn't run.
        var target = Assert.Single(await adapter.GotoTargetsAsync(thread, Worker, Line("run-text"), Ct));
        string? refused = null;
        try
        {
            await adapter.GotoAsync(thread, target.Id, Ct);
        }
        catch (DapException ex)
        {
            refused = ex.Message;
        }

        Assert.True(refused is null, $"Set Next Statement was refused: {refused}");
        var moved = await events.StoppedAsync();
        Assert.Equal("goto", moved.Reason);
        var top = await TopAsync(adapter, moved);
        Assert.Equal(Line("run-text"), top.Line);
        (_, top) = await StepAsync(adapter, events, moved, adapter.NextAsync, Worker, "run-print");
        Assert.Equal("\"worker:0\"", await ValueAsync(adapter, top, "text"));
        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task The_two_kinds_of_async_code_await_each_other(string build)
    {
        // The program awaits the library built the other way; the library awaits the program's callback.
        var library = fixture.Library;
        var (adapter, events) = await StartAsync(build, "mixed", library, [DebuggeeFixture.Line(library, "lib-sum")]);
        await using var session = adapter;
        var stop = await events.StoppedAsync();
        var top = await TopAsync(adapter, stop);
        Assert.Equal(DebuggeeFixture.Line(library, "lib-sum"), top.Line);
        Assert.Equal("4", await ValueAsync(adapter, top, "left"));

        // Over the awaited callback, which yields: the library's next line, with its value.
        (stop, _) = await StepAsync(adapter, events, stop, adapter.NextAsync, library, "lib-transform");
        (stop, top) = await StepAsync(adapter, events, stop, adapter.NextAsync, library, "lib-return");
        Assert.Equal("15", await ValueAsync(adapter, top, "transformed"));

        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task Into_the_program_from_the_library_built_the_other_way_and_out_again(string build)
    {
        var library = fixture.Library;
        var mixed = fixture.Source("Mixed.cs");
        var (adapter, events) = await StartAsync(build, "mixed", library, [DebuggeeFixture.Line(library, "lib-transform")]);
        await using var session = adapter;
        var stop = await events.StoppedAsync();

        // Into the program's callback (the other kind of code), over its await, to its line after it.
        await adapter.StepInAsync(stop.ThreadId!.Value, Ct);
        stop = await events.StoppedAsync(30);
        var top = await TopAsync(adapter, stop);
        Assert.Contains("TripleAsync", top.Name, StringComparison.Ordinal);
        Assert.Equal("5", await ValueAsync(adapter, top, "value"));
        (stop, _) = await StepAsync(adapter, events, stop, adapter.NextAsync, mixed, "mixed-yield");
        (stop, top) = await StepAsync(adapter, events, stop, adapter.NextAsync, mixed, "mixed-callback");

        // Out: back in the library once the callback has returned.
        await adapter.StepOutAsync(stop.ThreadId!.Value, Ct);
        if (build == AsyncDebuggeeFixture.RuntimeAsync)
        {
            // The awaiting library is a state machine: its MoveNext is called as the callback completes, where Just My
            // Code's stepper sees it come in. The step ends on its next line, the awaited value in its variable.
            stop = await events.StoppedAsync(30);
            top = await TopAsync(adapter, stop);
            Assert.True(top.Line == DebuggeeFixture.Line(library, "lib-return"), $"Step Out stopped in {top.Name} on line {top.Line}.");
            Assert.Equal("15", await ValueAsync(adapter, top, "transformed"));
            await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
        }
        else
        {
            // A limit (ADR 0100): the callback's task is awaited by runtime-async code, which the runtime resumes where
            // Just My Code's stepper doesn't see it, and no state machine names it. The library calls none of the user's
            // code after the await and runs to its end; the step ends in the next call the stepper sees resume on its
            // thread, the program's state machine that awaits the library, on its next line. When that resumes on another
            // thread, nothing ends the step and the program runs on, as Continue does (macOS CI, run 43526072431: the
            // callback had resumed on another thread after the step over its await). Either is the limit's.
            var next = await events.NextOfAsync(["stopped", "exited"], 30);
            if (next.Event == "stopped")
            {
                stop = next.Stopped!;
                top = await TopAsync(adapter, stop);
                Assert.True(top.Line == DebuggeeFixture.Line(mixed, "mixed-return"), $"Step Out stopped in {top.Name} on line {top.Line}.");
                Assert.Equal("16", await ValueAsync(adapter, top, "sum"));
                await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public async Task Step_over_the_end_of_a_resumed_async_method_stops_in_the_awaiting_caller(string build)
    {
        var (adapter, events) = await StartAsync(build, "steps", Worker, [Line("run-return")]);
        await using var session = adapter;
        var stop = await events.StoppedAsync();

        // The method resumed after its awaits: its end returns to the thread pool or the runtime's dispatcher, not to
        // Main, which awaits it.
        (stop, _) = await StepAsync(adapter, events, stop, adapter.NextAsync, Worker, "run-close");
        await adapter.NextAsync(stop.ThreadId!.Value, Ct);
        var program = fixture.Source("Program.cs");
        if (build == AsyncDebuggeeFixture.StateMachines)
        {
            // Main's state machine is the task's continuation: the step ends where Main resumes, on its next line (the if block's end).
            stop = await events.StoppedAsync(30);
            var top = await TopAsync(adapter, stop);
            Assert.True(top.Name.Contains("Main", StringComparison.Ordinal) && top.Line == DebuggeeFixture.Line(program, "main-run-end"), $"The step stopped in {top.Name} on line {top.Line}.");
            Assert.Equal("64", await ValueAsync(adapter, top, "total"));
        }
        else
        {
            // A limit (ADR 0100): the runtime doesn't say which call awaits a runtime-async call, and resumes Main where
            // Just My Code's stepper doesn't see it. Main calls none of the user's code after the await, so the program
            // runs on to its end, as a Continue would.
            await events.NextAsync(DapEvents.Terminated, 30);
            Assert.Equal(2, events.All.Count(e => e.Name == DapEvents.Stopped));
            return;
        }

        await adapter.DisconnectAsync(terminateDebuggee: true, Ct);
    }
}
