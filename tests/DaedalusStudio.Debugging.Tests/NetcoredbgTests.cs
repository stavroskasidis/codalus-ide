namespace DaedalusStudio.Debugging.Tests;

/// <summary>The DAP client against the real netcoredbg (ADR 0007), on samples/Debugging.</summary>
public sealed class NetcoredbgTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>
{
    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token).Token;

    private async Task<(DebugAdapter Adapter, EventLog Events)> StartAsync()
    {
        var path = DebuggeeFixture.Adapter;
        Assert.SkipWhen(path is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        var adapter = await DebugAdapter.StartAsync(path!, Timeout());
        adapter.Log += (_, line) => Console.Error.WriteLine("[netcoredbg] " + line);
        return (adapter, new EventLog(adapter));
    }

    private LaunchArguments Launch(params string[] args) =>
        new(DotnetHost, ["exec", debuggee.Dll, .. args], debuggee.Folder, []);

    private static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
                .First(File.Exists);

    [Fact]
    public async Task Capabilities_are_reported()
    {
        var (adapter, _) = await StartAsync();
        await using (adapter)
        {
            var c = adapter.Capabilities;
            Console.Error.WriteLine(
                $"configurationDone={c.SupportsConfigurationDoneRequest} conditional={c.SupportsConditionalBreakpoints} hit={c.SupportsHitConditionalBreakpoints} " +
                $"log={c.SupportsLogPoints} function={c.SupportsFunctionBreakpoints} setVariable={c.SupportsSetVariable} exceptionInfo={c.SupportsExceptionInfoRequest} " +
                $"terminate={c.SupportsTerminateRequest} filters={string.Join(",", c.ExceptionBreakpointFilters?.Select(f => f.Filter) ?? [])}");
            Assert.True(c.SupportsConfigurationDoneRequest);
        }
    }

    [Fact]
    public async Task Conditional_breakpoints_locals_evaluation_stepping_and_exit()
    {
        var (adapter, events) = await StartAsync();
        await using (adapter)
        {
            await adapter.LaunchAsync(Launch(), Timeout());
            await adapter.Initialized.WaitAsync(Timeout());
            var loop = DebuggeeFixture.Line(debuggee.Program, "loop");
            var breakpoints = await adapter.SetBreakpointsAsync(debuggee.Program, [new SourceBreakpoint(loop, Condition: "i == 2")], Timeout());
            Assert.Single(breakpoints);
            await adapter.ConfigurationDoneAsync(Timeout());

            var stopped = await events.StoppedAsync();
            Assert.Equal("breakpoint", stopped.Reason);
            var thread = stopped.ThreadId!.Value;
            var frames = await adapter.StackTraceAsync(thread, 20, Timeout());
            var top = frames.StackFrames[0];
            Assert.Equal(loop, top.Line);
            Assert.Equal(debuggee.Program, top.Source?.Path, ignoreCase: OperatingSystem.IsWindows());

            Assert.Equal("2", (await adapter.EvaluateAsync("i", top.Id, "watch", Timeout())).Result);
            Assert.Equal("3", (await adapter.EvaluateAsync("total", top.Id, "watch", Timeout())).Result); // 1 + 2
            var scopes = await adapter.ScopesAsync(top.Id, Timeout());
            var locals = await adapter.VariablesAsync(scopes[0].VariablesReference, Timeout());
            Assert.Contains(locals, v => v.Name == "numbers" && v.VariablesReference > 0);
            Assert.Contains(locals, v => v is { Name: "total", Value: "3" });

            await adapter.NextAsync(thread, Timeout());
            stopped = await events.StoppedAsync();
            Assert.Equal("step", stopped.Reason);

            await adapter.SetBreakpointsAsync(debuggee.Program, [], Timeout());
            await adapter.ContinueAsync(thread, Timeout());
            var exited = DebugAdapter.Read((await events.NextAsync(DapEvents.Exited)).Body, DapJsonContext.Default.ExitedEvent)!;
            DebuggeeFixture.AssertExitCode(15, exited.ExitCode);
            Assert.Contains("total=15 Ada is 36", events.Output, StringComparison.Ordinal);
        }
    }
}
