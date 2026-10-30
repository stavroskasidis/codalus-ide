using System.Diagnostics;
using System.Threading.Channels;
using Codalus.Workbench.Build;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>M14a on the real netcoredbg (ADR 0021): exception settings, Run to Cursor, client-side breakpoint features, modules, setExpression.</summary>
public sealed class DebuggerDepthTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
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

    private string Depth => Path.Combine(debuggee.Folder, "Depth.cs");

    private int Line(string marker) => DebuggeeFixture.Line(Depth, marker);

    private static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
                .First(File.Exists);

    private Task LaunchAsync() =>
        _debug.LaunchAsync(new LaunchPlan(DotnetHost, $"exec \"{debuggee.Dll}\" --depth", debuggee.Folder, new Dictionary<string, string>(), null), TestContext.Current.CancellationToken);

    private async Task<StopInfo> NextStopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            return await _stops.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The program did not stop. State: {_debug.State}. Output:\n{Output}");
        }
    }

    private async Task<int?> EndedAsync() => await _ended.Task.WaitAsync(TimeSpan.FromSeconds(60));

    private async Task<string> EvaluateAsync(string expression) => (await _debug.EvaluateAsync(expression, "watch", TestContext.Current.CancellationToken)).Result;

    [Fact]
    public async Task Exception_settings_break_on_listed_types_with_the_inner_exception_chain()
    {
        _debug.ExceptionSettings.Set("System.InvalidOperationException", true);
        await LaunchAsync();

        var stop = await NextStopAsync();
        Assert.Equal("exception", stop.Reason);
        Assert.Equal(Line("depth-outer"), _debug.SelectedFrame?.Line);
        var details = stop.Exception?.Details;
        Assert.Equal("System.InvalidOperationException", details?.FullTypeName);
        var inner = Assert.Single(details?.InnerException ?? []);
        Assert.Equal("System.ArgumentException", inner.FullTypeName);
        Assert.Equal("the inner one", inner.Message);

        // Every type but FormatException: nothing else is thrown, so the program runs to its end.
        _debug.ExceptionSettings.SetBreakOnOthers(true);
        _debug.ExceptionSettings.Set("System.FormatException", false);
        await _debug.ApplyExceptionSettingsAsync();
        await _debug.ContinueAsync();
        DebuggeeFixture.AssertExitCode(70, await EndedAsync());
        Assert.False(_stops.Reader.TryRead(out _));
        Assert.Contains("format", Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Module_conditions_skip_exceptions_thrown_in_other_modules()
    {
        // ArgumentException is thrown in Debuggee.dll, which its condition excludes; InvalidOperationException's includes it.
        _debug.ExceptionSettings.Set("System.ArgumentException", true);
        _debug.ExceptionSettings.SetCondition("System.ArgumentException", "!Debuggee.dll");
        _debug.ExceptionSettings.Set("System.InvalidOperationException", true);
        _debug.ExceptionSettings.SetCondition("System.InvalidOperationException", "Debug*.dll, Other.dll");
        await LaunchAsync();

        var stop = await NextStopAsync();
        if (_debug.Frames is [{ ModuleId: { ValueKind: System.Text.Json.JsonValueKind.String } module }, ..] && module.GetString() is "")
        {
            // M30b gap: netcoredbg 3.2.0 gives the frames of a .NET 11 program no module, so a module condition can't
            // tell where an exception was thrown. TODO(issue): a netcoredbg release that supports .NET 11.
            await _debug.StopAsync();
            Assert.Skip("netcoredbg reports no module for the stack frames of a .NET 11 program (M30b gap)");
        }

        Assert.Equal("System.InvalidOperationException", stop.Exception?.Details?.FullTypeName);
        Assert.Equal(Line("depth-outer"), _debug.SelectedFrame?.Line);
        await _debug.StopAsync();
    }

    [Theory]
    [InlineData(null, "Any.dll", true)]
    [InlineData("MyApp.dll", "myapp.dll", true)]
    [InlineData("MyApp.dll", "Other.dll", false)]
    [InlineData("!System.*", "System.Private.CoreLib.dll", false)]
    [InlineData("!System.*", "MyApp.dll", true)]
    [InlineData("My?pp.dll, !MyApp.dll", "MyApp.dll", false)]
    [InlineData("MyApp.dll", null, false)]
    public void Module_conditions_match_names_with_wildcards(string? condition, string? module, bool expected)
    {
        var settings = new ExceptionSettings();
        settings.SetCondition("System.Exception", condition);
        Assert.Equal(expected, settings.ModuleAllows("System.Exception", module));
        Assert.True(settings.ModuleAllows("System.FormatException", module)); // no condition

        var loaded = new ExceptionSettings();
        loaded.Load(false, null, settings.Conditions());
        Assert.Equal(condition, loaded.ConditionOf("System.Exception"));
    }

    [Fact]
    public void Exception_settings_become_filter_conditions()
    {
        var settings = new ExceptionSettings();
        Assert.Equal("user-unhandled | ", Normalize(settings.ToFilters()));

        settings.Set("System.FormatException", true);
        settings.Set("My.Custom", true);
        Assert.Equal("user-unhandled | all:My.Custom,System.FormatException", Normalize(settings.ToFilters()));
        Assert.True(settings.Breaks("My.Custom"));
        Assert.False(settings.Breaks("System.TimeoutException"));

        settings.SetAll(true);
        Assert.Equal("all user-unhandled | ", Normalize(settings.ToFilters()));

        settings.Set("System.FormatException", false);
        Assert.Equal("user-unhandled | all:!System.FormatException", Normalize(settings.ToFilters()));
        Assert.True(settings.Breaks("Unlisted.Exception"));

        var saved = settings.Changes();
        var loaded = new ExceptionSettings();
        loaded.Load(settings.BreakOnOthers, saved);
        Assert.Equal(Normalize(settings.ToFilters()), Normalize(loaded.ToFilters()));

        static string Normalize((string[] Filters, ExceptionFilterOptions[] Options) f) =>
            string.Join(" ", f.Filters) + " | " + string.Join(" ", f.Options.Select(o => o.FilterId + ":" + o.Condition));
    }

    [Fact]
    public async Task Run_to_cursor_stops_at_the_line_and_force_skips_breakpoints()
    {
        _debug.Breakpoints.Set(new BreakpointSpec(Depth, Line("depth-start")));
        _debug.Breakpoints.Set(new BreakpointSpec(Depth, Line("depth-loop")));
        await LaunchAsync();
        var first = await NextStopAsync();
        Assert.Equal(Line("depth-start"), _debug.SelectedFrame?.Line);
        Assert.NotNull(first.Elapsed);

        // Plain Run to Cursor stops at the loop's breakpoint first.
        await _debug.RunToCursorAsync(Depth, Line("depth-square-call"));
        await NextStopAsync();
        Assert.Equal(Line("depth-loop"), _debug.SelectedFrame?.Line);
        Assert.False(_debug.IsRunningToCursor);

        // Force Run to Cursor goes past it.
        await _debug.RunToCursorAsync(Depth, Line("depth-square-call"), force: true);
        var stop = await NextStopAsync();
        Assert.Equal(Line("depth-square-call"), _debug.SelectedFrame?.Line);
        Assert.Equal("1", await EvaluateAsync("i"));
        Assert.True(stop.Elapsed > TimeSpan.Zero);

        // The hidden breakpoint is gone: the next stop is the loop breakpoint again (i = 2).
        await _debug.ContinueAsync();
        await NextStopAsync();
        Assert.Equal(Line("depth-loop"), _debug.SelectedFrame?.Line);
        Assert.Equal("2", await EvaluateAsync("i"));
        await _debug.StopAsync();
        await EndedAsync();
    }

    [Fact]
    public async Task Temporary_dependent_and_function_breakpoints()
    {
        _debug.Breakpoints.Set(new BreakpointSpec(Depth, Line("depth-loop"), Temporary: true));
        _debug.Breakpoints.Set(new BreakpointSpec(Depth, Line("depth-end"), DependsOn: new BreakpointLocation(Depth, Line("depth-start"))));
        _debug.Breakpoints.SetFunction(new FunctionBreakpointSpec("Debuggee.Depth.Square", Condition: "value == 3"));
        await LaunchAsync();

        await NextStopAsync();
        Assert.Equal(Line("depth-loop"), _debug.SelectedFrame?.Line);
        Assert.DoesNotContain(_debug.Breakpoints.All, b => b.Line == Line("depth-loop")); // temporary: removed after its stop
        Assert.True(_debug.Breakpoints.StateOf(_debug.Breakpoints.Functions.Single())?.Verified);

        await _debug.ContinueAsync();
        await NextStopAsync();
        Assert.Contains("Square", _debug.SelectedFrame?.Name, StringComparison.Ordinal);
        Assert.Equal("3", await EvaluateAsync("value"));

        // depth-end depends on depth-start, which has no breakpoint (never hit): the program runs to its end.
        await _debug.ContinueAsync();
        DebuggeeFixture.AssertExitCode(70, await EndedAsync());
        Assert.False(_stops.Reader.TryRead(out _));
    }

    [Fact]
    public async Task A_dependent_breakpoint_breaks_once_its_dependency_was_hit()
    {
        _debug.Breakpoints.Set(new BreakpointSpec(Depth, Line("depth-square"), DependsOn: new BreakpointLocation(Depth, Line("depth-loop"))));
        _debug.Breakpoints.Set(new BreakpointSpec(Depth, Line("depth-loop"), HitCondition: "2"));
        await LaunchAsync();

        await NextStopAsync();
        Assert.Equal(Line("depth-loop"), _debug.SelectedFrame?.Line);
        Assert.Equal("2", await EvaluateAsync("i"));

        await _debug.ContinueAsync();
        await NextStopAsync();
        Assert.Contains("Square", _debug.SelectedFrame?.Name, StringComparison.Ordinal);
        Assert.Equal("2", await EvaluateAsync("value")); // not 1: the dependency was hit on the second iteration
        await _debug.StopAsync();
        await EndedAsync();
    }

    /// <summary>
    /// A long array's ranges (M56) stand on netcoredbg: it counts an array's elements as named variables (it reports no
    /// indexed ones), and a variables request's start and count page them, without a filter.
    /// </summary>
    [Fact]
    public async Task Netcoredbg_counts_a_long_arrays_elements_and_pages_them()
    {
        _debug.Breakpoints.Set(new BreakpointSpec(Depth, Line("depth-end")));
        await LaunchAsync();
        await NextStopAsync();

        var locals = await _debug.VariablesAsync((await _debug.ScopesAsync(_debug.SelectedFrame!))[0].VariablesReference);
        var readings = Assert.Single(locals, v => v.Name == "readings");
        Assert.Equal(("int[]", 2500, (int?)null), (readings.Type, readings.NamedVariables, readings.IndexedVariables));
        var evaluated = await _debug.EvaluateAsync("readings", "watch", TestContext.Current.CancellationToken);
        Assert.Equal(2500, evaluated.NamedVariables);

        var page = await _debug.VariablesAsync(readings.VariablesReference, new VariablesPage(1000, 1000));
        Assert.Equal(1000, page.Count);
        Assert.Equal(("[1000]", "1000", "readings[1000]"), (page[0].Name, page[0].Value, page[0].EvaluateName));
        Assert.Equal("[1999]", page[^1].Name);
        var end = await _debug.VariablesAsync(readings.VariablesReference, new VariablesPage(2400, 1000));
        Assert.Equal(["[2400]", "[2499]"], new[] { end[0].Name, end[^1].Name });
        Assert.Equal(100, end.Count);
        await _debug.StopAsync();
        await EndedAsync();
    }

    [Fact]
    public async Task Set_expression_modules_threads_and_long_strings()
    {
        _debug.Breakpoints.Set(new BreakpointSpec(Depth, Line("depth-end")));
        await LaunchAsync();
        await NextStopAsync();

        Assert.Contains(_debug.Modules, m => m.Name == "Debuggee.dll" && m.SymbolStatus is not null);
        Assert.Contains(_debug.Modules, m => m.Name == "System.Private.CoreLib.dll");

        var result = await _debug.SetExpressionAsync("settings.Retries", "7");
        Assert.Equal("7", result.Value);
        Assert.Equal("7", await EvaluateAsync("settings.Retries"));
        Assert.Contains("\\\"engines\\\"", await EvaluateAsync("json"), StringComparison.Ordinal);

        var tops = await _debug.TopFramesAsync(TestContext.Current.CancellationToken);
        Assert.Contains(tops.Values, f => f.Line == Line("depth-end"));
        await _debug.StopAsync();
        await EndedAsync();
    }
}
