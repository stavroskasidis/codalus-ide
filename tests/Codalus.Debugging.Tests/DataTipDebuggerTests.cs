using Codalus.Debugging.Host;
using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// The data tip's rows on netcoredbg (D44): the companion's member list groups an object's members, and the recordings
/// in <c>Recorded/</c> come from here (<see cref="Record_the_data_tips_fixtures"/>).
/// </summary>
public sealed class DataTipDebuggerTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
{
    private readonly TypeAttributes _attributes = new();
    private DebugService _debug = null!;

    public ValueTask InitializeAsync()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        _debug = new DebugService(() => DebuggeeFixture.Adapter)
        {
            Displays = new DebuggerDisplays((type, modules, _) => Task.FromResult<Codalus.Debugging.Companion.TypeDisplayResult?>(_attributes.Find(type, modules))) { Budget = TimeSpan.FromSeconds(60) },
            IsUserModule = path => path?.EndsWith("Debuggee.dll", StringComparison.OrdinalIgnoreCase) == true,
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_debug is not null)
        {
            await _debug.DisposeAsync();
        }

        _attributes.Dispose();
    }

    private async Task PauseAsync(string dll, string source, DebugService debug, string? host = null)
    {
        var stopped = new TaskCompletionSource();
        debug.StateChanged += (_, _) =>
        {
            if (debug.State == DebugState.Stopped)
            {
                stopped.TrySetResult();
            }
        };
        debug.Breakpoints.Set(new BreakpointSpec(source, DebuggeeFixture.Line(source, "datatip")));
        await debug.LaunchAsync(host ?? DebuggeeFixture.DotnetHost, ["exec", dll, "--datatip"], Path.GetDirectoryName(dll)!, new Dictionary<string, string>(), TestContext.Current.CancellationToken);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken);
        for (var i = 0; i < 200 && debug.SelectedFrame is null; i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(debug.SelectedFrame);
    }

    [Fact]
    public async Task Your_own_types_private_members_are_inline_and_a_librarys_are_grouped()
    {
        await PauseAsync(debuggee.Dll, Path.Combine(debuggee.Folder, "DataTip.cs"), _debug);
        var locals = await _debug.VariablesAsync((await _debug.ScopesAsync(_debug.SelectedFrame!))[0].VariablesReference);
        var inventory = Assert.Single(locals, v => v.Name == "inventory");
        var members = await _debug.MembersAsync(inventory.Type!, TestContext.Current.CancellationToken);
        Assert.NotNull(members);
        var rows = VariableGrouping.Group(await _debug.VariablesAsync(inventory.VariablesReference), members, _debug.IsUserCode);

        // Inline, in name order: the public members and your own private field, with the lock; then the groups.
        var logger = Assert.Single(rows, r => r.Variable.Name == "logger");
        Assert.True(logger.IsNonPublic && logger.IsUserCode);
        Assert.Equal(MemberKind.Field, logger.Kind);
        Assert.Equal(MemberKind.Property, Assert.Single(rows, r => r.Variable.Name == "Size").Kind);
        var session = Assert.Single(rows, r => r.Variable.Name == "Session");
        Assert.True(session.Threw, $"Session: {session.Variable.Value} ({session.Variable.Type}), declared {session.DeclaredType}");
        Assert.Equal(MemberKind.NonPublicGroup, rows[^2].Kind);
        Assert.Contains(rows[^2].Children!, r => r.Variable.Name == "items" && !r.IsUserCode);
        Assert.Equal(MemberKind.StaticGroup, rows[^1].Kind);
        var inline = rows.Take(rows.Count - 2).Select(r => r.Variable.Name).ToList();
        Assert.Equal(inline.Order(StringComparer.OrdinalIgnoreCase), inline);
        Assert.False(Assert.Single(rows, r => r.Variable.Name == "Size").CanWrite);

        // The static members: Summaries, a field.
        var statics = await _debug.VariablesAsync(rows[^1].Variable.VariablesReference);
        var staticRows = VariableGrouping.Statics(statics, await _debug.MembersAsync(rows[^1].Variable.EvaluateName!, TestContext.Current.CancellationToken), _debug.IsUserCode);
        Assert.Equal(MemberKind.Field, Assert.Single(staticRows, r => r.Variable.Name == "Summaries").Kind);
        await _debug.StopAsync();
    }

    /// <summary>
    /// Writes <c>Recorded/datatip-&lt;runtime&gt;.json</c> when <c>CODALUS_RECORD_DATATIP</c> names the folder: the
    /// Debuggee's <c>--datatip</c> locals as netcoredbg lists them, four levels deep. <c>CODALUS_RECORD_DATATIP_DLL</c>
    /// and <c>_SOURCE</c> debug a copy built for an older framework instead, run by <c>_HOST</c> (a dotnet with that
    /// runtime), labelled <c>_RUNTIME</c> (<c>net6.0</c>).
    /// </summary>
    [Fact]
    public async Task Record_the_data_tips_fixtures()
    {
        var folder = Environment.GetEnvironmentVariable("CODALUS_RECORD_DATATIP");
        Assert.SkipWhen(string.IsNullOrEmpty(folder), "Set CODALUS_RECORD_DATATIP to a folder to record the data tip's fixtures.");
        var dll = Environment.GetEnvironmentVariable("CODALUS_RECORD_DATATIP_DLL") is { Length: > 0 } other ? other : debuggee.Dll;
        var source = Environment.GetEnvironmentVariable("CODALUS_RECORD_DATATIP_SOURCE") is { Length: > 0 } file ? file : Path.Combine(debuggee.Folder, "DataTip.cs");
        var runtime = Environment.GetEnvironmentVariable("CODALUS_RECORD_DATATIP_RUNTIME") is { Length: > 0 } label ? label : "net11.0";
        await using var raw = new DebugService(() => DebuggeeFixture.Adapter); // netcoredbg's own answers: no [DebuggerDisplay]
        await PauseAsync(dll, source, raw, Environment.GetEnvironmentVariable("CODALUS_RECORD_DATATIP_HOST"));
        var recording = await RecordedVariables.RecordAsync(raw, runtime, depth: 4);
        Directory.CreateDirectory(folder!);
        await File.WriteAllTextAsync(Path.Combine(folder!, $"datatip-{runtime}.json"), RecordedVariables.Serialize(recording), TestContext.Current.CancellationToken);
        await raw.StopAsync();
    }
}
