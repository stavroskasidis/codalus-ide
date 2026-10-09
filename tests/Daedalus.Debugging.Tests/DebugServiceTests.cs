using System.Diagnostics;
using System.Threading.Channels;
using Daedalus.Workbench.Build;
using Daedalus.Workbench.Debugging;

namespace Daedalus.Debugging.Tests;

/// <summary>The workbench's debug sessions on the real netcoredbg (ADR 0007).</summary>
public sealed class DebugServiceTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
{
    private readonly Channel<StopInfo> _stops = Channel.CreateUnbounded<StopInfo>();
    private readonly TaskCompletionSource<int?> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly System.Text.StringBuilder _output = new();
    private DebugService _debug = null!;

    public ValueTask InitializeAsync()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        _debug = new DebugService(() => DebuggeeFixture.Adapter);
        _debug.Stopped += (_, stop) => _stops.Writer.TryWrite(stop);
        _debug.Ended += (_, code) => _ended.TrySetResult(code);
        _debug.Output += (_, o) =>
        {
            lock (_output)
            {
                _output.Append(o.Text);
            }
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_debug is not null)
        {
            await _debug.DisposeAsync();
        }
    }

    private string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    private static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
                .First(File.Exists);

    private LaunchPlan Plan(string arguments = "") =>
        new(DotnetHost, $"exec \"{debuggee.Dll}\" {arguments}".TrimEnd(), debuggee.Folder, new Dictionary<string, string>(), null);

    private async Task<StopInfo> NextStopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            return await _stops.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            var breakpoints = string.Join("; ", _debug.Breakpoints.All.Select(b => $"{b.Path}:{b.Line} {_debug.Breakpoints.StateOf(b)}"));
            throw new TimeoutException($"The program did not stop. State: {_debug.State}. Breakpoints: {breakpoints}. Output:\n{Output}");
        }
    }

    private async Task<int?> EndedAsync() => await _ended.Task.WaitAsync(TimeSpan.FromSeconds(60));

    private int Line(string marker) => DebuggeeFixture.Line(debuggee.Program, marker);

    [Fact]
    public async Task Hit_counts_are_emulated_and_the_program_runs_to_its_exit_code()
    {
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, Line("loop"), HitCondition: "3"));
        await _debug.LaunchAsync(Plan(), TestContext.Current.CancellationToken);

        var stop = await NextStopAsync();
        Assert.Equal("breakpoint", stop.Reason);
        Assert.Equal(DebugState.Stopped, _debug.State);
        Assert.Equal("2", (await _debug.EvaluateAsync("i", "watch", TestContext.Current.CancellationToken)).Result); // the third hit
        Assert.Equal(Line("loop"), _debug.SelectedFrame?.Line);
        Assert.True(_debug.Breakpoints.StateOf(_debug.Breakpoints.All.Single())?.Verified);

        await _debug.ContinueAsync();
        DebuggeeFixture.AssertExitCode(15, await EndedAsync());
        Assert.Equal(DebugState.Idle, _debug.State);
        Assert.Contains("total=15 Ada is 36", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logpoints_write_messages_without_stopping()
    {
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, Line("loop"), LogMessage: "i={i} total={total} in $FUNCTION"));
        await _debug.LaunchAsync(Plan(), TestContext.Current.CancellationToken);

        DebuggeeFixture.AssertExitCode(15, await EndedAsync());
        Assert.Contains("i=0 total=0 in", Output, StringComparison.Ordinal);
        Assert.Contains("i=4 total=10 in", Output, StringComparison.Ordinal);
        Assert.False(_stops.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Step_into_step_out_locals_and_set_variable()
    {
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, Line("call")));
        await _debug.LaunchAsync(Plan(), TestContext.Current.CancellationToken);
        await NextStopAsync();

        await _debug.StepIntoAsync();
        await NextStopAsync();
        Assert.Contains("Describe", _debug.SelectedFrame!.Name, StringComparison.Ordinal);
        Assert.Equal(debuggee.Types, _debug.SelectedFrame.Source?.Path, ignoreCase: OperatingSystem.IsWindows());
        var scopes = await _debug.ScopesAsync(_debug.SelectedFrame);
        var locals = await _debug.VariablesAsync(scopes[0].VariablesReference);
        var person = Assert.Single(locals, v => v.Name == "person");
        Assert.Contains(await _debug.VariablesAsync(person.VariablesReference), v => v is { Name: "Name", Value: "\"Ada\"" });

        await _debug.StepOutAsync();
        await NextStopAsync();
        Assert.Equal(Line("call"), _debug.SelectedFrame?.Line);

        // Change a local, then let the program print it.
        await _debug.StepOverAsync();
        await NextStopAsync();
        var mainLocals = await _debug.VariablesAsync((await _debug.ScopesAsync(_debug.SelectedFrame!))[0].VariablesReference);
        var reference = (await _debug.ScopesAsync(_debug.SelectedFrame!))[0].VariablesReference;
        Assert.Contains(mainLocals, v => v.Name == "total");
        await _debug.SetVariableAsync(reference, "total", "100");
        await _debug.ContinueAsync();
        DebuggeeFixture.AssertExitCode(100, await EndedAsync());
        Assert.Contains("total=100", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Threads_and_exceptions()
    {
        _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Types, DebuggeeFixture.Line(debuggee.Types, "worker")));
        await _debug.SetExceptionFiltersAsync([DebugService.ExceptionsAll]);
        await _debug.LaunchAsync(Plan(), TestContext.Current.CancellationToken);

        var stop = await NextStopAsync();
        Assert.Equal("breakpoint", stop.Reason);
        Assert.True(_debug.Threads.Count >= 2);
        Assert.Contains(_debug.Threads, t => t.Id == stop.ThreadId && t.Name.Contains("Worker", StringComparison.Ordinal));
        var main = _debug.Threads.First(t => t.Id != stop.ThreadId);
        await _debug.SelectThreadAsync(main.Id);
        Assert.Equal(main.Id, _debug.SelectedThreadId);
        Assert.NotEmpty(_debug.Frames);

        await _debug.ContinueAsync();
        stop = await NextStopAsync();
        Assert.Equal("exception", stop.Reason);
        Assert.Contains("boom", stop.Exception?.Details?.Message ?? stop.Exception?.Description ?? stop.Description ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(DebuggeeFixture.Line(debuggee.Types, "throw"), _debug.SelectedFrame?.Line);

        await _debug.StopAsync();
        await EndedAsync();
        Assert.Equal(DebugState.Idle, _debug.State);
    }

    [Fact]
    public async Task Attaching_to_a_running_process_and_detaching()
    {
        var signal = Path.Combine(Path.GetTempPath(), "daedalus-attach-" + Guid.NewGuid().ToString("N"));
        using var process = Process.Start(new ProcessStartInfo(DotnetHost, ["exec", debuggee.Dll, "--wait-for", signal])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        try
        {
            _debug.Breakpoints.Set(new BreakpointSpec(debuggee.Program, Line("done")));
            await _debug.AttachAsync(process.Id, TestContext.Current.CancellationToken);
            Assert.True(_debug.IsAttached);
            await File.WriteAllTextAsync(signal, "go", TestContext.Current.CancellationToken);

            var stop = await NextStopAsync();
            Assert.Equal("breakpoint", stop.Reason);
            Assert.Equal("15", (await _debug.EvaluateAsync("total", "repl", TestContext.Current.CancellationToken)).Result);

            await _debug.StopAsync(); // detaches: the program goes on
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Assert.Equal(15, process.ExitCode); // the process itself, not the adapter
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            File.Delete(signal);
        }
    }

    [Theory]
    [InlineData("exec \"C:\\a b\\x.dll\" one", new[] { "exec", "C:\\a b\\x.dll", "one" })]
    [InlineData("a \"b \\\"c\\\" d\" e\\f", new[] { "a", "b \"c\" d", "e\\f" })]
    [InlineData("  ", new string[0])]
    [InlineData("\"\"", new[] { "" })]
    public void Command_lines_split_like_the_runtime(string commandLine, string[] expected) =>
        Assert.Equal(expected, CommandLine.Split(commandLine));

    [Theory]
    [InlineData("3", 3, true)]
    [InlineData("3", 4, false)]
    [InlineData("== 2", 2, true)]
    [InlineData(">= 2", 5, true)]
    [InlineData(">= 2", 1, false)]
    [InlineData("% 2", 4, true)]
    [InlineData("% 2", 3, false)]
    public void Hit_conditions(string condition, int hits, bool met) =>
        Assert.Equal(met, BreakpointStore.HitConditionMet(condition, hits));
}
