namespace Codalus.Debugging.Tests;

/// <summary>
/// A func-eval that needs a class another thread is still initializing (<c>samples/Debugging/Debuggee --initializer</c>):
/// that thread is suspended inside the class constructor, so the evaluation would wait on the class's initialization
/// lock until it timed out (CI runs 43308888468 and 43572537779, docs/ci-flakes.md). The Codalus debugger lets the
/// thread run until the evaluation returns.
/// </summary>
public sealed class ClassConstructorEvalTests(DebuggeeFixture fixture) : IClassFixture<DebuggeeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_evaluation_that_needs_a_class_another_thread_is_initializing_returns()
    {
        var initializer = Path.Combine(fixture.Folder, "Initializer.cs");
        await using var adapter = await DebugAdapter.StartAsync(DebuggeeFixture.CodalusAdapter(), Ct);
        var events = new EventLog(adapter);
        await adapter.LaunchAsync(new NetFxDebugArguments(DebuggeeFixture.DotnetHost, ["exec", fixture.Dll, "--initializer"], fixture.Folder, Type: "coreclr"), DapJsonContext.Default.NetFxDebugArguments, Ct);
        await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        await adapter.SetBreakpointsAsync(initializer, [new SourceBreakpoint(DebuggeeFixture.Line(initializer, "initializer-stop"))], Ct);
        await adapter.ConfigurationDoneAsync(Ct);

        var stop = await events.StoppedAsync();
        var top = (await adapter.StackTraceAsync(stop.ThreadId!.Value, 1, Ct)).StackFrames[0];
        Assert.Equal(DebuggeeFixture.Line(initializer, "initializer-stop"), top.Line);

        // Slow's constructor sleeps for two seconds on the Initializer thread; the call waits for it, well inside the 5 s.
        Assert.Equal("84", (await adapter.EvaluateAsync("Debuggee.Slow.Twice()", top.Id, "watch", Ct)).Result);

        // The program goes on as usual: it exits with Slow.Twice() - 84.
        await adapter.ContinueAsync(stop.ThreadId!.Value, Ct);
        Assert.Equal(0, (await events.NextAsync(DapEvents.Exited)).Body.GetProperty("exitCode").GetInt32());
    }
}
