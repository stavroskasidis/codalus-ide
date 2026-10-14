using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Codalus.Debugging.Tests;

/// <summary>
/// The Mono debug adapter (<c>codalus-monodbg</c>, M20b, ADR 0065) as the shell drives it: a real process through the
/// DAP client. What needs a Mono runtime (stopping, stepping, evaluating) is the emulator test's (MobileShellTests).
/// </summary>
public sealed class MonoAdapterTests
{
    private static string AdapterPath
    {
        get
        {
            var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            var configuration = output.Parent!.Name; // bin/<configuration>/net10.0
            var root = output;
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "Codalus.slnx")))
            {
                root = root.Parent;
            }

            return Path.Combine(root!.FullName, "src", "Codalus.Debugging.Mono", "bin", configuration, "net10.0", "codalus-monodbg.dll");
        }
    }

    private static Task<DebugAdapter> StartAsync() =>
        DebugAdapter.StartAsync(new DebugAdapterStartInfo("dotnet", [AdapterPath]), TestContext.Current.CancellationToken);

    [Fact]
    public async Task It_answers_initialize_with_its_capabilities_and_is_ready_for_breakpoints()
    {
        await using var adapter = await StartAsync();

        Assert.True(adapter.Capabilities.SupportsConfigurationDoneRequest);
        Assert.True(adapter.Capabilities.SupportsConditionalBreakpoints);
        Assert.Contains(adapter.Capabilities.ExceptionBreakpointFilters ?? [], f => f.Filter == "all");
        await adapter.Initialized.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Breakpoints set before the attach are kept for the session.
        var file = Path.Combine(Path.GetTempPath(), "Page.cs");
        var set = await adapter.SetBreakpointsAsync(file, [new SourceBreakpoint(10), new SourceBreakpoint(12, Condition: "count > 1")], TestContext.Current.CancellationToken);
        Assert.Equal(2, set.Length);
        Assert.All(set, b => Assert.True(b.Verified));
        Assert.Equal(12, set[1].Line);
    }

    [Fact]
    public async Task An_attach_where_no_agent_listens_ends_the_session_with_the_address()
    {
        await using var adapter = await StartAsync();
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        } // closed: nothing listens there now

        var said = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.EventReceived += e =>
        {
            if (e.Name == "output" && e.Body.GetProperty("category").GetString() == "important")
            {
                said.TrySetResult(e.Body.GetProperty("output").GetString()!);
            }
            else if (e.Name == "terminated")
            {
                terminated.TrySetResult();
            }

            return Task.CompletedTask;
        };

        // The attach is answered at once (the breakpoints that follow must be set before the app runs); the session
        // then ends with the reason.
        await adapter.AttachAsync(new MonoAttachArguments(port, ConnectTimeout: 1), DapJsonContext.Default.MonoAttachArguments, TestContext.Current.CancellationToken);
        Assert.Contains($"127.0.0.1:{port}", await said.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await terminated.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Requests_it_doesnt_know_fail_and_the_session_goes_on()
    {
        await using var adapter = await StartAsync();
        var error = await Assert.ThrowsAsync<DapException>(() => adapter.Connection.RequestAsync("readMemory", new EmptyArguments(), DapJsonContext.Default.EmptyArguments, TestContext.Current.CancellationToken));
        Assert.Contains("readMemory", error.Message, StringComparison.Ordinal);

        var threads = await adapter.Connection.RequestAsync("threads", new EmptyArguments(), DapJsonContext.Default.EmptyArguments, TestContext.Current.CancellationToken);
        Assert.Equal(JsonValueKind.Array, threads.GetProperty("threads").ValueKind); // no session yet: no threads
    }
}
