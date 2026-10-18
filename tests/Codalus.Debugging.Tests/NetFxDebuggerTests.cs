using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// M43b (ADR 0076), on Windows only (D31): the desktop-CLR debugger adapter (<c>codalus-debughost --netfx-dap</c>)
/// against the real .NET Framework 4.8 runtime and its ICorDebug. It launches samples/Debugging/NetFxDebuggee, binds a
/// breakpoint before its module loads, stops there with the stack, locals, members and getters, evaluates expressions
/// (members, operators, indexers, method calls through func-eval), steps, reads an async method's hoisted variables,
/// stops on the caught exception of the type it filters, forwards the output and Debug.WriteLine, and ends with the exit
/// code; through the debug service, F5's path, and attached to a running process found by the Attach to Process list.
/// Elsewhere these are skipped: the DAP layer is tested on every OS against a fake ICorDebug (NetFxSessionTests).
/// </summary>
public sealed class NetFxDebuggerTests(NetFxDebuggeeFixture fixture) : IClassFixture<NetFxDebuggeeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DapJsonContext s_json = DapJsonContext.Default;

    private void RequireWindows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), ".NET Framework programs run and debug only on Windows (D31).");
        fixture.RequireExe();
        Assert.Null(DesktopClr.BitnessProblem(fixture.Exe!, Environment.Is64BitProcess));
    }

    private static DebugAdapterStartInfo Adapter() =>
        new("dotnet", [Path.Combine(AppContext.BaseDirectory, "codalus-debughost.dll"), DesktopClr.AdapterArgument], AppContext.BaseDirectory);

    private int Line(string marker) => DebuggeeFixture.Line(fixture.Program, marker);

    [Fact]
    public async Task Breakpoints_stacks_variables_evaluation_steps_async_exceptions_and_the_end_on_the_desktop_clr()
    {
        RequireWindows();
        var events = Channel.CreateUnbounded<DapEvent>();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var output = new System.Text.StringBuilder();
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Ct);
        adapter.EventReceived += e =>
        {
            if (e.Name == DapEvents.Output)
            {
                lock (output)
                {
                    output.Append(e.Body.GetProperty("output").GetString());
                }
            }

            return events.Writer.WriteAsync(e).AsTask();
        };
        adapter.Log += (_, line) => log.Enqueue(line);

        await adapter.LaunchAsync(new NetFxDebugArguments(fixture.Exe, [], fixture.Folder), s_json.NetFxDebugArguments, Ct);
        await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var set = await adapter.SetBreakpointsAsync(fixture.Program, [new SourceBreakpoint(Line("netfx-sum"))], Ct);
        var id = Assert.Single(set).Id;
        await adapter.SetExceptionBreakpointsAsync(["user-unhandled"], Ct, [new ExceptionFilterOptions("all", "System.InvalidOperationException")]);
        await adapter.ConfigurationDoneAsync(Ct);

        // The breakpoint in Cart.Total, bound when the module loaded.
        var stopped = await NextAsync(events, DapEvents.Stopped, log);
        Assert.Equal("breakpoint", stopped.GetProperty("reason").GetString());
        Assert.Contains(id!.Value, stopped.GetProperty("hitBreakpointIds").EnumerateArray().Select(e => e.GetInt32()));
        var thread = stopped.GetProperty("threadId").GetInt32();
        Assert.Contains(await adapter.ThreadsAsync(Ct), t => t.Id == thread && t.Name == "Main thread");

        var stack = await adapter.StackTraceAsync(thread, 20, Ct);
        var top = stack.StackFrames[0];
        Assert.Equal("NetFxDebuggee.Cart.Total", top.Name);
        Assert.Equal(Line("netfx-sum"), top.Line);
        Assert.Equal(Path.GetFullPath(fixture.Program), Path.GetFullPath(top.Source!.Path!), ignoreCase: true);
        Assert.Contains(stack.StackFrames, f => f.Name == "NetFxDebuggee.Program.Main" && f.Line == Line("netfx-total"));

        // Locals, this, auto-properties, a getter, an enum, an array, the item.
        var scope = Assert.Single(await adapter.ScopesAsync(top.Id, Ct));
        var locals = await adapter.VariablesAsync(scope.VariablesReference, Ct);
        Assert.Equal("0", locals.Single(v => v.Name == "sum").Value);
        var self = locals.Single(v => v.Name == "this");
        Assert.Equal("{NetFxDebuggee.Cart}", self.Value);
        var members = await adapter.VariablesAsync(self.VariablesReference, Ct);
        Assert.Equal("\"Ada\"", members.Single(v => v.Name == "Owner").Value);
        Assert.Equal("2", members.Single(v => v.Name == "Count").Value);
        Assert.Equal("Busy | Loud", members.Single(v => v.Name == "Mood").Value);
        Assert.Equal("{int[3]}", members.Single(v => v.Name == "_codes").Value);
        Assert.Contains(await adapter.VariablesAsync(locals.Single(v => v.Name == "item").VariablesReference, Ct), v => v is { Name: "Name", Value: "\"Tea\"" });

        // Evaluation, func-eval included.
        Assert.Equal("3", (await adapter.EvaluateAsync("item.Price", top.Id, "watch", Ct)).Result);
        Assert.Equal("3", (await adapter.EvaluateAsync("Owner.Length", top.Id, "watch", Ct)).Result);
        Assert.Equal("4", (await adapter.EvaluateAsync("Twice(Count)", top.Id, "watch", Ct)).Result);
        Assert.Equal("9", (await adapter.EvaluateAsync("_codes[1] + 1", top.Id, "watch", Ct)).Result);
        Assert.Equal("true", (await adapter.EvaluateAsync("Count > 1 && Owner == \"Ada\"", top.Id, "watch", Ct)).Result);
        Assert.Equal("\"ADA\"", (await adapter.EvaluateAsync("Owner.ToUpper()", top.Id, "watch", Ct)).Result);
        Assert.Equal("\"Tea\"", (await adapter.EvaluateAsync("_items[0].Name", top.Id, "watch", Ct)).Result);
        Assert.Equal("5", (await adapter.EvaluateAsync("Math.Max(item.Price, 5)", top.Id, "watch", Ct)).Result);
        await Assert.ThrowsAsync<DapException>(() => adapter.EvaluateAsync("nothingHere", top.Id, "watch", Ct));

        // Step over: still in Total, on another line.
        await adapter.NextAsync(thread, Ct);
        Assert.Equal("step", (await NextAsync(events, DapEvents.Stopped, log)).GetProperty("reason").GetString());
        var stepped = (await adapter.StackTraceAsync(thread, 1, Ct)).StackFrames[0];
        Assert.Equal("NetFxDebuggee.Cart.Total", stepped.Name);

        // The async method: its hoisted variables.
        await adapter.SetBreakpointsAsync(fixture.Program, [new SourceBreakpoint(Line("netfx-async"))], Ct);
        await adapter.ContinueAsync(thread, Ct);
        var inAsync = await NextAsync(events, DapEvents.Stopped, log);
        Assert.Equal("breakpoint", inAsync.GetProperty("reason").GetString());
        var asyncThread = inAsync.GetProperty("threadId").GetInt32();
        var asyncTop = (await adapter.StackTraceAsync(asyncThread, 1, Ct)).StackFrames[0];
        Assert.Equal(Line("netfx-async"), asyncTop.Line);
        var asyncLocals = await adapter.VariablesAsync(Assert.Single(await adapter.ScopesAsync(asyncTop.Id, Ct)).VariablesReference, Ct);
        Assert.Equal("5", asyncLocals.Single(v => v.Name == "value").Value);
        Assert.Equal("2", asyncLocals.Single(v => v.Name == "factor").Value);
        Assert.Equal("10", (await adapter.EvaluateAsync("value * factor", asyncTop.Id, "watch", Ct)).Result);

        // Without breakpoints: on to the caught exception of the filtered type.
        await adapter.SetBreakpointsAsync(fixture.Program, [], Ct);
        await adapter.ContinueAsync(asyncThread, Ct);
        var exception = await NextAsync(events, DapEvents.Stopped, log);
        Assert.Equal("exception", exception.GetProperty("reason").GetString());
        var info = await adapter.ExceptionInfoAsync(exception.GetProperty("threadId").GetInt32(), Ct);
        Assert.Equal("System.InvalidOperationException", info.ExceptionId);
        Assert.Equal("The till is closed.", info.Description);
        Assert.Contains("Checkout", info.Details?.StackTrace ?? string.Empty, StringComparison.Ordinal);
        var throwing = (await adapter.StackTraceAsync(exception.GetProperty("threadId").GetInt32(), 1, Ct)).StackFrames[0];
        Assert.Equal(Line("netfx-throw"), throwing.Line);

        // And the program ends, its output and Debug.WriteLine through the Debug Console.
        await adapter.ContinueAsync(exception.GetProperty("threadId").GetInt32(), Ct);
        var exited = await NextAsync(events, DapEvents.Exited, log);
        Assert.Equal(3, exited.GetProperty("exitCode").GetInt32());
        await NextAsync(events, DapEvents.Terminated, log);
        string text;
        lock (output)
        {
            text = output.ToString();
        }

        Assert.Contains("Ada: 5", text, StringComparison.Ordinal);
        Assert.Contains("doubled: 10", text, StringComparison.Ordinal);
        Assert.Contains("caught: The till is closed.", text, StringComparison.Ordinal);
        Assert.Contains("netfx-log", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_debug_service_debugs_a_net48_program_from_its_launch_plan()
    {
        // F5's path: a launch plan whose program is a managed .exe goes to the desktop-CLR adapter.
        RequireWindows();
        await using var debug = new DebugService(() => null) { DesktopClrAdapter = Adapter };
        var stops = Channel.CreateUnbounded<StopInfo>();
        var ended = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Stopped += (_, stop) => stops.Writer.TryWrite(stop);
        debug.Ended += (_, code) => ended.TrySetResult(code);
        debug.Breakpoints.Toggle(fixture.Program, Line("netfx-checkout"));
        Assert.True(debug.IsDesktopClrProgram(fixture.Exe!));

        await debug.LaunchAsync(new Codalus.Workbench.Build.LaunchPlan(fixture.Exe!, string.Empty, fixture.Folder, new Dictionary<string, string>(), null), Ct);
        var stop = await stops.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(120), Ct);
        Assert.Equal("breakpoint", stop.Reason);
        Assert.Equal(Line("netfx-checkout"), debug.SelectedFrame?.Line);
        Assert.True(debug.Breakpoints.StateOf(debug.Breakpoints.All.Single())?.Verified);
        Assert.NotNull(debug.ProcessId);
        Assert.Equal("5", (await debug.EvaluateAsync("total", "watch", Ct)).Result);
        Assert.Equal("10", (await debug.EvaluateAsync("doubled", "watch", Ct)).Result);
        Assert.Equal("\"Ada\"", (await debug.EvaluateAsync("cart.Owner", "watch", Ct)).Result);
        Assert.Contains(debug.Modules, m => m.Name == "NetFxDebuggee.exe" && m.IsUserCode == true);

        await debug.StopAsync();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
    }

    [Fact]
    public async Task Attach_to_a_running_net48_process_stops_evaluates_and_detaches()
    {
        RequireWindows();
        var start = new ProcessStartInfo(fixture.Exe!, "wait") { UseShellExecute = false, RedirectStandardOutput = true, WorkingDirectory = fixture.Folder };
        using var program = Process.Start(start)!;
        try
        {
            Assert.Equal("waiting", await program.StandardOutput.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct));

            // The Attach to Process list knows it runs the desktop CLR, and is read quickly (it took more than the minute
            // the program used to wait on a loaded CI runner, and the program had ended when its turn came).
            var watch = Stopwatch.StartNew();
            var entry = ProcessList.Get().Single(p => p.Id == program.Id);
            watch.Stop();
            Assert.False(program.HasExited, $"The program ended while the process list was read ({watch.Elapsed}).");
            Assert.True(entry.IsDesktopClr, $"Not a desktop-CLR process (the list took {watch.Elapsed}); its modules: {string.Join(", ", Codalus.Platform.ProcessModules.Names(program.Id) ?? ["unknown"])}");
            Assert.True(entry.IsDotnet);
            Assert.Equal(".NET Framework", entry.Runtime);
            Assert.True(DesktopClr.RunsDesktopClr(program.Id));

            await using var debug = new DebugService(() => null) { DesktopClrAdapter = Adapter };
            var stops = Channel.CreateUnbounded<StopInfo>();
            debug.Stopped += (_, stop) => stops.Writer.TryWrite(stop);
            debug.Breakpoints.Toggle(fixture.Program, Line("netfx-tick"));
            await debug.AttachAsync(program.Id, Ct);
            Assert.True(debug.IsAttached);
            var stop = await stops.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal("breakpoint", stop.Reason);
            Assert.Equal(Line("netfx-tick"), debug.SelectedFrame?.Line);
            var i = int.Parse((await debug.EvaluateAsync("i", "watch", Ct)).Result, System.Globalization.CultureInfo.InvariantCulture);
            Assert.InRange(i, 0, 6000);

            // Stop detaches: the program goes on.
            await debug.StopAsync();
            await Task.Delay(500, Ct);
            Assert.False(program.HasExited);
        }
        finally
        {
            if (!program.HasExited)
            {
                program.Kill();
            }
        }
    }

    private static async Task<JsonElement> NextAsync(Channel<DapEvent> events, string name, System.Collections.Concurrent.ConcurrentQueue<string> log)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            while (true)
            {
                var e = await events.Reader.ReadAsync(timeout.Token);
                if (e.Name == name)
                {
                    return e.Body;
                }

                if (e.Name == DapEvents.Terminated && name == DapEvents.Stopped)
                {
                    Assert.Fail("The program ended before stopping.\n" + string.Join('\n', log));
                }
            }
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            Assert.Fail($"No '{name}' event within 120 s.\n" + string.Join('\n', log));
            throw;
        }
    }
}
