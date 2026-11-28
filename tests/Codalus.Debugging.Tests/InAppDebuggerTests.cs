using System.Net;
using System.Net.Sockets;
using Codalus.Debugging.Host.CoreClr;
using Codalus.Debugging.Host.NetFx;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// ADR 0117, on every OS: the IDE's side of the in-app debugger, and the adapter's side of it without a device. The
/// adapter serves on a TCP port here (as in the app) with a stand-in ICorDebug; the IDE connects, trying until the port
/// answers, launches in the app's own process with the sources on its side, and the adapter's caller learns when the
/// breakpoints are set (the app's start is held until then). The device itself is checked by hand, as ADR 0065's are.
/// </summary>
public sealed class InAppDebuggerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task The_ide_connects_once_the_app_serves_and_launches_in_its_process_with_the_sources_here()
    {
        var port = FreePort();
        var process = new FakeProcess();
        var debugger = new FakeDebugger(process);
        var configured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;

        // The IDE starts trying before the app serves: its build and install come first. It asks for the address again on
        // each try (a device's tunnel comes up with the install). The app serves only once a try
        // has been refused and another begun: on Windows a refusal on the loopback takes about 2 s (its SYN retries), and
        // a listener started during the first try was found by it (CI, PR #581's run 46140083626).
        var connecting = InAppConnection.ConnectAsync(_ => { Interlocked.Increment(ref asked); return Task.FromResult<string?>("127.0.0.1"); }, port, TimeSpan.FromMilliseconds(50), TimeSpan.FromMinutes(1), Ct);
        var refused = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref asked) < 2)
        {
            Assert.True(refused.Elapsed < TimeSpan.FromSeconds(30), "The first try wasn't refused in 30 s.");
            Assert.False(connecting.IsCompleted, "It connected with nothing listening.");
            await Task.Delay(20, Ct);
        }

        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var serving = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Ct);
            using var stream = client.GetStream();
            return await NetFxAdapter.RunAsync(stream, stream, TextWriter.Null, debugger, configured: session => session.ContinueWith(_ => configured.TrySetResult(), TaskScheduler.Default));
        }, Ct);

        try
        {
            var connection = await connecting.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await using var debug = new DebugService(() => null);
            await debug.LaunchInAppAsync("App", connection, "iPhone", Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

            Assert.True(debugger.InProcess is { InProcess: true, SourcesOnClient: true, Program: "App" }, $"The launch: {debugger.InProcess}");
            await configured.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Assert.Equal(DebugState.Running, debug.State);

            await debug.StopAsync();
        }
        finally
        {
            listener.Stop();
            await serving.WaitAsync(TimeSpan.FromSeconds(30), Ct).ContinueWith(_ => { }, Ct);
        }
    }

    [Fact]
    public async Task A_device_without_a_tunnel_or_an_app_that_never_serves_ends_the_wait_with_why()
    {
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            InAppConnection.ConnectAsync(_ => Task.FromResult<string?>(null), FreePort(), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(200), Ct));
        Assert.Contains("the device has no tunnel address", error.Message, StringComparison.Ordinal);

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InAppConnection.ConnectAsync(_ => Task.FromResult<string?>("127.0.0.1"), FreePort(), TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(1), stop.Token));
    }

    [Fact]
    public void The_runtimes_debugging_libraries_are_beside_it_as_frameworks_on_a_device_and_files_in_a_simulator()
    {
        // In this OS's own separators (it runs on iOS; Windows CI wrote back slashes, run 46140083626).
        var app = Path.Combine(Path.GetTempPath(), "App.app");
        Assert.Equal(
            Path.Combine(app, "Frameworks", "libmscordaccore.framework", "libmscordaccore"),
            IosDeviceRuntime.Sibling(Path.Combine(app, "Frameworks", "libcoreclr.framework", "libcoreclr"), "libmscordaccore"));
        Assert.Equal(Path.Combine(app, "libmscordbi.dylib"), IosDeviceRuntime.Sibling(Path.Combine(app, "libcoreclr.dylib"), "libmscordbi"));
    }

    [Fact]
    public void The_apps_environment_loads_the_debugger_as_a_profiler_by_a_path_known_before_the_install()
    {
        var environment = InAppDebugger.Environment(50123).ToDictionary(v => v.Key, v => v.Value);
        Assert.Equal("1", environment["DOTNET_ENABLE_PROFILING"]);
        Assert.Equal(InAppDebugger.ProfilerClassId, environment["DOTNET_PROFILER"]);
        Assert.Equal("@executable_path/Frameworks/CodalusDebugger.framework/CodalusDebugger", environment["DOTNET_PROFILER_PATH"]);
        Assert.Equal("50123", environment[InAppDebugger.PortVariable]);
        Assert.False(environment.ContainsKey(InAppDebugger.HoldVariable));
        Assert.Equal("5", InAppDebugger.Environment(1, holdSeconds: 5).Single(v => v.Key == InAppDebugger.HoldVariable).Value);

        // The DAP traffic in the app's console, when the IDE's own adapter log is asked for.
        Assert.DoesNotContain(environment.Keys, k => k == InAppDebugger.TraceVariable);
        Assert.Equal("1", InAppDebugger.Environment(1, trace: true).Single(v => v.Key == InAppDebugger.TraceVariable).Value);
    }
}
