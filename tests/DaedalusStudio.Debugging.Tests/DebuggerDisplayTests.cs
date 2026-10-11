using DaedalusStudio.Debugging.Host;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Debugging.Tests;

/// <summary>[DebuggerDisplay] and [DebuggerBrowsable] in Locals and Watch, on netcoredbg (M14b).</summary>
public sealed class DebuggerDisplayTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
{
    private readonly TypeAttributes _attributes = new();
    private DebugService _debug = null!;

    public ValueTask InitializeAsync()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
        _debug = new DebugService(() => DebuggeeFixture.Adapter)
        {
            // The companion host's lookup, in-process.
            // A slow CI runner's first evaluations take longer than the shell's 3 s budget.
            Displays = new DebuggerDisplays((type, modules, _) => Task.FromResult<DaedalusStudio.Debugging.Companion.TypeDisplayResult?>(_attributes.Find(type, modules)))
            {
                Budget = TimeSpan.FromSeconds(60),
            },
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _debug.DisposeAsync();
        _attributes.Dispose();
    }

    [Fact]
    public async Task An_object_shows_its_display_string_and_hides_what_the_attributes_hide()
    {
        var stopped = new TaskCompletionSource();
        _debug.StateChanged += (_, _) =>
        {
            if (_debug.State == DebugState.Stopped)
            {
                stopped.TrySetResult();
            }
        };
        var depth = Path.Combine(debuggee.Folder, "Depth.cs");
        _debug.Breakpoints.Set(new BreakpointSpec(depth, DebuggeeFixture.Line(depth, "depth-start")));
        await _debug.LaunchAsync(DotnetHost, ["exec", debuggee.Dll, "--depth"], debuggee.Folder, new Dictionary<string, string>(), TestContext.Current.CancellationToken);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await WaitForAsync(() => _debug.SelectedFrame is not null);

        var locals = await _debug.VariablesAsync((await _debug.ScopesAsync(_debug.SelectedFrame!))[0].VariablesReference);
        var order = Assert.Single(locals, v => v.Name == "order");
        Assert.Equal("Order 7: Ada (1 lines)", order.Value); // {Customer,nq} without quotes

        var members = await _debug.VariablesAsync(order.VariablesReference);
        Assert.Contains(members, m => m.Name == "Id");
        Assert.DoesNotContain(members, m => m.Name is "_version" or "Internal"); // [DebuggerBrowsable(Never)], field and property

        var watched = await _debug.EvaluateAsync("order", "watch", TestContext.Current.CancellationToken);
        Assert.Equal("Order 7: Ada (1 lines)", watched.Result);

        // A type without the attribute keeps netcoredbg's value.
        Assert.Equal("{Debuggee.Settings}", Assert.Single(locals, v => v.Name == "settings").Value);
        await _debug.StopAsync();
    }

    [Theory]
    [InlineData("Order {Id}: {Customer,nq}", "t:Order |e:Id|t:: |e:Customer,nq")]
    [InlineData("Count = {Count}", "t:Count = |e:Count")]
    [InlineData(@"{Items[0]} \{literal\}", "e:Items[0]|t: {literal}")]
    [InlineData("{Format(1, 2),nq}", "e:Format(1, 2),nq")]
    [InlineData("No braces", "t:No braces")]
    public void Display_strings_split_into_text_and_expressions(string format, string expected)
    {
        var parts = DebuggerDisplays.Parse(format).Select(p => p.Expression is null ? "t:" + p.Text : "e:" + p.Expression + (p.NoQuotes ? ",nq" : string.Empty));
        Assert.Equal(expected, string.Join("|", parts));
    }

    [Theory]
    [InlineData("{Debuggee.Order}", "Debuggee.Order", true)]
    [InlineData("{int[3]}", "int[]", false)]
    [InlineData("\"text\"", "string", false)]
    [InlineData("42", "int", false)]
    public void Only_object_values_get_a_display(string value, string type, bool expected)
    {
        Assert.Equal(expected, DebuggerDisplays.IsObjectValue(value, type));
    }

    private static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
            ? host
            : (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
                .Select(d => Path.Combine(d, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
                .First(File.Exists);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }
}
