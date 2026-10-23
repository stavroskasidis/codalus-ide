using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Codalus.Platform;

namespace Codalus.Debugging.Tests;

/// <summary>samples/Debugging/MonoDebuggee copied to a temp folder and built once: a .NET 8 console app on the Mono runtime.</summary>
public sealed class MonoDebuggeeFixture : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Codalus.Platform.RealPath.Resolve(Path.GetTempPath()), "codalus-mono-" + Guid.NewGuid().ToString("N"));

    public string Folder => Path.Combine(_root, "MonoDebuggee");

    public string Program => Path.Combine(Folder, "Program.cs");

    /// <summary>The self-contained app (its apphost), or null when the build failed (the reason is in <see cref="BuildOutput"/>).</summary>
    public string? App { get; private set; }

    public string BuildOutput { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var source = Path.Combine(DebuggeeFixture.RepoRoot, "samples", "Debugging", "MonoDebuggee");
        Directory.CreateDirectory(Folder);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(Folder, Path.GetFileName(file)));
        }

        File.Copy(Path.Combine(DebuggeeFixture.RepoRoot, "samples", "Directory.Build.props"), Path.Combine(_root, "Directory.Build.props"));
        File.Copy(Path.Combine(DebuggeeFixture.RepoRoot, "samples", "Directory.Packages.props"), Path.Combine(_root, "Directory.Packages.props"));
        Codalus.Tests.FixtureSdk.Pin(_root);
        var start = new ProcessStartInfo("dotnet", ["build", Folder, "-nologo", "-v", "quiet"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        BuildOutput = await output + await error;
        var app = Directory.EnumerateFiles(Path.Combine(Folder, "bin"), OperatingSystem.IsWindows() ? "MonoDebuggee.exe" : "MonoDebuggee", SearchOption.AllDirectories).FirstOrDefault();
        App = process.ExitCode == 0 ? app : null;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// M20b (ADR 0065): the Mono debugger adapter (<c>codalus-debughost --mono-dap</c>) against a real Mono runtime's
/// soft-debugger agent, the one .NET 8 to 10 apps run on Android and iOS: it connects (retrying until the agent
/// answers, as through adb's forward), binds a breakpoint in a type that loads later, stops there with the stack,
/// locals, fields and auto-properties, evaluates member paths, steps, stops on a caught exception when asked, and
/// ends with the program.
/// </summary>
public sealed class MonoDebuggerTests(MonoDebuggeeFixture fixture) : IClassFixture<MonoDebuggeeFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The debuggee, built; skipped where .NET 8 publishes no desktop Mono runtime pack for this machine (Windows on Arm).</summary>
    private void RequireApp()
    {
        Assert.SkipWhen(fixture.App is null && fixture.BuildOutput.Contains("NU1102", StringComparison.Ordinal), "No desktop Mono runtime pack for this machine:\n" + fixture.BuildOutput);
        Assert.True(fixture.App is not null, "The Mono debuggee didn't build:\n" + fixture.BuildOutput);
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static DebugAdapterStartInfo Adapter()
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "codalus-debughost.dll");
        return new DebugAdapterStartInfo("dotnet", [dll, "--mono-dap"], AppContext.BaseDirectory);
    }

    private Process StartApp(int port)
    {
        var start = new ProcessStartInfo(fixture.App!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["MONO_ENV_OPTIONS"] = $"--debugger-agent=transport=dt_socket,server=y,address=127.0.0.1:{port},suspend=y";
        var process = Process.Start(start)!;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    [Fact]
    public async Task Breakpoints_stacks_variables_evaluation_steps_and_exceptions_on_the_mono_runtime()
    {
        RequireApp();
        var port = FreePort();
        var events = System.Threading.Channels.Channel.CreateUnbounded<DapEvent>();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Ct);
        adapter.EventReceived += e => events.Writer.WriteAsync(e).AsTask();
        adapter.Log += (_, line) => log.Enqueue(line);
        Assert.True(adapter.Capabilities.SupportsConfigurationDoneRequest);
        Assert.Contains(adapter.Capabilities.ExceptionBreakpointFilters!, f => f.Filter == "all");

        // The adapter retries until the agent answers, so the app may start after the launch request.
        var launch = adapter.LaunchAsync(new MonoDebugArguments(Port: port, TimeoutSeconds: 60), DapJsonContext.Default.MonoDebugArguments, Ct);
        await Task.Delay(1500, Ct);
        using var app = StartApp(port);
        try
        {
            await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            await launch.WaitAsync(TimeSpan.FromSeconds(60), Ct);

            // A breakpoint in Cart.Total, whose type hasn't loaded yet: bound when it loads.
            var sum = DebuggeeFixture.Line(fixture.Program, "mono-sum");
            var set = await adapter.SetBreakpointsAsync(fixture.Program, [new SourceBreakpoint(sum)], Ct);
            var id = Assert.Single(set).Id;
            await adapter.SetExceptionBreakpointsAsync(["all"], Ct);
            await adapter.ConfigurationDoneAsync(Ct);

            var stopped = await NextStopAsync(events, log);
            Assert.Equal("breakpoint", stopped.GetProperty("reason").GetString());
            Assert.Contains(id!.Value, stopped.GetProperty("hitBreakpointIds").EnumerateArray().Select(e => e.GetInt32()));
            var thread = stopped.GetProperty("threadId").GetInt32();

            var stack = await adapter.StackTraceAsync(thread, 20, Ct);
            var top = stack.StackFrames[0];
            Assert.Equal("MonoDebuggee.Cart.Total", top.Name);
            Assert.Equal((fixture.Program, sum), (top.Source?.Path, top.Line));
            Assert.Contains(stack.StackFrames, f => f.Line == DebuggeeFixture.Line(fixture.Program, "mono-total"));

            // Locals, this, its fields and auto-properties; the item's record properties.
            var scope = Assert.Single(await adapter.ScopesAsync(top.Id, Ct));
            var locals = await adapter.VariablesAsync(scope.VariablesReference, Ct);
            Assert.Equal("0", locals.Single(v => v.Name == "sum").Value);
            var self = locals.Single(v => v.Name == "this");
            Assert.Equal("{MonoDebuggee.Cart}", self.Value);
            var members = await adapter.VariablesAsync(self.VariablesReference, Ct);
            Assert.Equal("\"Ada\"", members.Single(v => v.Name == "Owner").Value);
            Assert.Equal("2", members.Single(v => v.Name == "Count").Value);
            var item = locals.Single(v => v.Name == "item");
            Assert.Contains(await adapter.VariablesAsync(item.VariablesReference, Ct), v => v is { Name: "Name", Value: "\"Tea\"" });

            // Evaluation: names and member paths.
            Assert.Equal("3", (await adapter.EvaluateAsync("item.Price", top.Id, "watch", Ct)).Result);
            Assert.Equal("\"Ada\"", (await adapter.EvaluateAsync("this.Owner", top.Id, "watch", Ct)).Result);
            Assert.Equal("\"Ada\"", (await adapter.EvaluateAsync("Owner", top.Id, "watch", Ct)).Result);
            await Assert.ThrowsAsync<DapException>(() => adapter.EvaluateAsync("nothingHere", top.Id, "watch", Ct));

            // Step over: still in Total, past the line.
            await adapter.NextAsync(thread, Ct);
            var stepped = await NextStopAsync(events, log);
            Assert.Equal("step", stepped.GetProperty("reason").GetString());
            Assert.Equal("MonoDebuggee.Cart.Total", (await adapter.StackTraceAsync(thread, 1, Ct)).StackFrames[0].Name);

            // Without the breakpoint: on to the caught exception.
            await adapter.SetBreakpointsAsync(fixture.Program, [], Ct);
            await adapter.ContinueAsync(thread, Ct);
            var exception = await NextStopAsync(events, log);
            Assert.Equal("exception", exception.GetProperty("reason").GetString());
            var info = await adapter.ExceptionInfoAsync(exception.GetProperty("threadId").GetInt32(), Ct);
            Assert.Equal("System.InvalidOperationException", info.ExceptionId);
            Assert.Equal("The till is closed.", info.Description);

            // And the program ends.
            await adapter.ContinueAsync(exception.GetProperty("threadId").GetInt32(), Ct);
            await WaitForAsync(events, DapEvents.Terminated, log);
            Assert.True(app.WaitForExit(30_000));
        }
        finally
        {
            if (!app.HasExited)
            {
                ProcessTree.Kill(app);
            }
        }
    }

    [Fact]
    public async Task A_debug_session_through_the_debug_service_stops_evaluates_and_ends()
    {
        // The path F5 on a MAUI project takes: DebugService.LaunchAdapterAsync with the adapter and its arguments.
        RequireApp();
        var port = FreePort();
        await using var debug = new Codalus.Workbench.Debugging.DebugService(() => null);
        var stops = System.Threading.Channels.Channel.CreateUnbounded<Codalus.Workbench.Debugging.StopInfo>();
        var ended = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Stopped += (_, stop) => stops.Writer.TryWrite(stop);
        debug.Ended += (_, code) => ended.TrySetResult(code);
        debug.Breakpoints.Toggle(fixture.Program, DebuggeeFixture.Line(fixture.Program, "mono-checkout"));

        using var app = StartApp(port);
        try
        {
            await debug.LaunchAdapterAsync("MonoDebuggee (desktop Mono)", Adapter(), JsonSerializer.SerializeToElement(new MonoDebugArguments(Port: port, TimeoutSeconds: 60), DapJsonContext.Default.MonoDebugArguments), Ct);
            var stop = await stops.Reader.ReadAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.Equal("breakpoint", stop.Reason);
            Assert.Equal(DebuggeeFixture.Line(fixture.Program, "mono-checkout"), debug.SelectedFrame?.Line);
            Assert.True(debug.Breakpoints.StateOf(debug.Breakpoints.All.Single())?.Verified);
            Assert.Equal("5", (await debug.EvaluateAsync("total", "watch", Ct)).Result);
            Assert.Equal("2", (await debug.EvaluateAsync("cart.Count", "watch", Ct)).Result);

            await debug.StopAsync();
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
            Assert.True(app.WaitForExit(30_000), "Stopping ends the program.");
        }
        finally
        {
            if (!app.HasExited)
            {
                ProcessTree.Kill(app);
            }
        }
    }

    [Fact]
    public async Task Without_a_runtime_the_launch_says_so_after_its_timeout()
    {
        await using var adapter = await DebugAdapter.StartAsync(Adapter(), Ct);
        var error = await Assert.ThrowsAsync<DapException>(() => adapter.LaunchAsync(new MonoDebugArguments(Port: FreePort(), TimeoutSeconds: 2), DapJsonContext.Default.MonoDebugArguments, Ct));
        Assert.Contains("No Mono runtime answered", error.Message, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> NextStopAsync(System.Threading.Channels.Channel<DapEvent> events, System.Collections.Concurrent.ConcurrentQueue<string> log) =>
        await WaitForAsync(events, DapEvents.Stopped, log);

    private static async Task<JsonElement> WaitForAsync(System.Threading.Channels.Channel<DapEvent> events, string name, System.Collections.Concurrent.ConcurrentQueue<string> log)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
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
            Assert.Fail($"No '{name}' event within 60 s.\n" + string.Join('\n', log));
            throw;
        }
    }
}
