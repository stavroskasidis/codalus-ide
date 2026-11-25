namespace Codalus.Debugging.Tests;

/// <summary>
/// #547: Debug → Step Into New Instance launches a program stopped at its entry point's first line, the launch's
/// <c>stopAtEntry</c>. The Codalus debugger finds the user's <c>Main</c> (top-level statements; an async <c>Main</c>'s
/// state machine, or the method itself under runtime async) and stops there once, with the reason <c>entry</c>.
/// </summary>
public sealed class StopAtEntryTests(DebuggeeFixture fixture, AsyncDebuggeeFixture asyncFixture) : IClassFixture<DebuggeeFixture>, IClassFixture<AsyncDebuggeeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DapJsonContext s_json = DapJsonContext.Default;

    private static async Task<(DebugAdapter Adapter, EventLog Events)> LaunchAsync(string dll, string folder, string[] arguments, string? source = null, params SourceBreakpoint[] breakpoints)
    {
        var adapter = await DebugAdapter.StartAsync(DebuggeeFixture.CodalusAdapter(), Ct);
        try
        {
            var events = new EventLog(adapter);
            await adapter.LaunchAsync(new NetFxDebugArguments(DebuggeeFixture.DotnetHost, ["exec", dll, .. arguments], folder, Type: "coreclr") { StopAtEntry = true }, s_json.NetFxDebugArguments, Ct);
            await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            if (source is not null)
            {
                await adapter.SetBreakpointsAsync(source, breakpoints, Ct);
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

    [Fact]
    public async Task Top_level_statements_stop_at_their_first_line_then_run_on()
    {
        var (adapter, events) = await LaunchAsync(fixture.Dll, fixture.Folder, []);
        await using var _ = adapter;

        var stop = await events.StoppedAsync();
        Assert.Equal("entry", stop.Reason);
        var top = await TopAsync(adapter, stop);
        Assert.Equal(fixture.Program, top.Source?.Path);
        Assert.Equal(DebuggeeFixture.Line(fixture.Program, "first"), top.Line);

        // Once only: the program runs to its end.
        await adapter.ContinueAsync(stop.ThreadId!.Value, Ct);
        var (name, _) = await events.NextOfAsync([DapEvents.Stopped, DapEvents.Exited], seconds: 90);
        Assert.Equal(DapEvents.Exited, name);
    }

    [Fact]
    public async Task A_breakpoint_on_the_first_line_is_the_one_stop_there()
    {
        var first = DebuggeeFixture.Line(fixture.Program, "first");
        var (adapter, events) = await LaunchAsync(fixture.Dll, fixture.Folder, [], fixture.Program, new SourceBreakpoint(first));
        await using var _ = adapter;

        var stop = await events.StoppedAsync();
        Assert.Equal("breakpoint", stop.Reason);
        Assert.Equal(first, (await TopAsync(adapter, stop)).Line);

        await adapter.ContinueAsync(stop.ThreadId!.Value, Ct);
        var (name, _) = await events.NextOfAsync([DapEvents.Stopped, DapEvents.Exited], seconds: 90);
        Assert.Equal(DapEvents.Exited, name);
    }

    [Theory]
    [InlineData("AsyncDebuggee")]
    [InlineData("AsyncDebuggee.RuntimeAsync")]
    public async Task An_async_Main_stops_in_its_own_code(string build)
    {
        var (dll, folder) = asyncFixture.Program(build);
        var (adapter, events) = await LaunchAsync(dll, folder, ["chain"]);
        await using var _ = adapter;

        var stop = await events.StoppedAsync();
        Assert.Equal("entry", stop.Reason);
        var top = await TopAsync(adapter, stop);
        var program = asyncFixture.Source("Program.cs");
        Assert.Equal(program, top.Source?.Path);
        Assert.Contains("Main", top.Name, StringComparison.Ordinal);

        // At Main's opening brace or its first statement, before any of its code ran.
        var lines = File.ReadAllLines(program);
        var main = Array.FindIndex(lines, l => l.Contains("static async Task<int> Main", StringComparison.Ordinal)) + 1;
        var firstStatement = Array.FindIndex(lines, l => l.Contains("var scenario =", StringComparison.Ordinal)) + 1;
        Assert.InRange(top.Line, main + 1, firstStatement);
    }
}
