using System.Reflection;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// #75: the debugger attaches to a Blazor Web App's server that <c>dotnet watch</c> started, and owns it as a launch
/// does (Stop ends it, Restart is allowed), while its start fails as an attach does (it detaches, never ends the
/// server). Before <c>dotnet watch</c> restarts the server, the session leaves it running. The adapter is the scripted
/// netcoredbg stand-in, which writes the requests of an attach to a process with a request log; the "server" is a
/// stand-in process.
/// </summary>
public sealed class OwnedAttachTests : IDisposable
{
    private readonly System.Diagnostics.Process _server;
    private readonly string _requests;
    private readonly string _script;

    public OwnedAttachTests()
    {
        _server = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", [ScriptedAdapter, "--program"]) { UseShellExecute = false, RedirectStandardInput = true })!;
        var id = _server.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _requests = Path.Combine(Path.GetTempPath(), "codalus-scripted-requests-" + id);
        _script = Path.Combine(Path.GetTempPath(), "codalus-scripted-attach-" + id);
        File.WriteAllText(_requests, string.Empty);
    }

    public void Dispose()
    {
        try
        {
            _server.Kill();
        }
        catch (InvalidOperationException)
        {
        }

        _server.Dispose();
        File.Delete(_requests);
        File.Delete(_script);
    }

    private static string Metadata(string key) =>
        typeof(OwnedAttachTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string ScriptedAdapter => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.LspConformance.Server", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    private static string NetcoredbgStandIn => Path.ChangeExtension(ScriptedAdapter, OperatingSystem.IsWindows() ? ".exe" : null);

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token).Token;

    private static LaunchPlan Plan => new("App", string.Empty, AppContext.BaseDirectory, new Dictionary<string, string>(), "http://localhost:5274/");

    private string[] Requests() => File.ReadAllLines(_requests);

    private async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}. Requests:\n{string.Join("\n", Requests())}");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task The_session_owns_the_server_it_attached_to_and_stop_ends_it()
    {
        await using var debug = new DebugService(() => NetcoredbgStandIn);
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Ended += (_, _) => ended.TrySetResult(debug.StopRequested);
        await debug.AttachOwnedCompoundAsync(_server.Id, "BlazorHosted", Plan, Timeout());

        Assert.Contains(Requests(), r => r.StartsWith("attach ", StringComparison.Ordinal) && r.Contains($"\"processId\":{_server.Id}", StringComparison.Ordinal));
        Assert.True(debug.IsCompound);
        Assert.False(debug.IsAttached); // Restart is allowed, the status says "Debugging"
        Assert.False(debug.CanDetach); // Detach All isn't offered: the session owns the server
        Assert.Equal(_server.Id, debug.ProcessId);
        var process = Assert.Single(debug.Processes);
        Assert.Equal((_server.Id, DebugProcess.Launched, "BlazorHosted"), (process.Id, process.Kind, process.Name));

        await debug.StopAsync();
        Assert.True(await ended.Task.WaitAsync(Timeout()));
        await WaitForAsync(() => Requests().Any(r => r.StartsWith("disconnect", StringComparison.Ordinal)), "the disconnect");
        Assert.Contains(Requests(), r => r.StartsWith("disconnect", StringComparison.Ordinal) && r.Contains("\"terminateDebuggee\":true", StringComparison.Ordinal));
        Assert.True(debug.Completion.IsCompleted);
    }

    [Fact]
    public async Task Releasing_the_server_detaches_and_leaves_it_running()
    {
        await using var debug = new DebugService(() => NetcoredbgStandIn);
        var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        debug.Ended += (_, _) => ended.TrySetResult(debug.StopRequested);
        await debug.AttachOwnedCompoundAsync(_server.Id, "BlazorHosted", Plan, Timeout());

        await debug.ReleaseOwnedServerAsync();
        Assert.False(await ended.Task.WaitAsync(Timeout())); // not a Stop: the session follows the next server
        Assert.Equal(DebugState.Idle, debug.State);
        await WaitForAsync(() => Requests().Any(r => r.StartsWith("disconnect", StringComparison.Ordinal)), "the disconnect");
        Assert.All(Requests().Where(r => r.StartsWith("disconnect", StringComparison.Ordinal)), r => Assert.Contains("\"terminateDebuggee\":false", r, StringComparison.Ordinal));
        Assert.False(_server.HasExited);
    }

    [Fact]
    public async Task A_failed_attach_detaches_and_never_ends_the_server()
    {
        // (A start netcoredbg gave up on waits for the runtime while the server runs, as any attach: DebugAdapter.AttachAsync.)
        await File.WriteAllTextAsync(_script, "fail", TestContext.Current.CancellationToken);
        await using var debug = new DebugService(() => NetcoredbgStandIn);
        await Assert.ThrowsAsync<InvalidOperationException>(() => debug.AttachOwnedCompoundAsync(_server.Id, "BlazorHosted", Plan, Timeout()));
        Assert.Equal(DebugState.Idle, debug.State);
        await WaitForAsync(() => Requests().Any(r => r.StartsWith("disconnect", StringComparison.Ordinal)), "the disconnect");
        Assert.Single(Requests(), r => r.StartsWith("attach ", StringComparison.Ordinal));
        Assert.All(Requests().Where(r => r.StartsWith("disconnect", StringComparison.Ordinal)), r => Assert.Contains("\"terminateDebuggee\":false", r, StringComparison.Ordinal));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(_server.HasExited);
    }
}
