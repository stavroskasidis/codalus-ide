using DaedalusStudio.Debugging.Host;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Debugging.Tests;

/// <summary>The grid and image visualizers on netcoredbg (M14b).</summary>
public sealed class CollectionVisualizerTests(DebuggeeFixture debuggee) : IClassFixture<DebuggeeFixture>, IAsyncLifetime
{
    private readonly TypeAttributes _attributes = new();
    private DebugService _debug = null!;

    public ValueTask InitializeAsync()
    {
        _debug = new DebugService(() => DebuggeeFixture.Adapter)
        {
            Displays = new DebuggerDisplays((type, modules, _) => Task.FromResult<DaedalusStudio.Debugging.Companion.TypeDisplayResult?>(_attributes.Find(type, modules))),
        };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _debug.DisposeAsync();
        _attributes.Dispose();
    }

    [Fact]
    public async Task Collections_show_as_grids_and_byte_arrays_as_images()
    {
        Assert.SkipWhen(DebuggeeFixture.Adapter is null, "netcoredbg is not installed (eng/get-netcoredbg.sh).");
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
        string? TypeOf(string name) => Assert.Single(locals, v => v.Name == name).Type;

        // A List<int>, read through ToArray(): an index and a value per element.
        Assert.True(CollectionVisualizers.IsCollection(TypeOf("squares")), TypeOf("squares"));
        var squares = await CollectionVisualizers.GridAsync(_debug, "squares", TypeOf("squares"), TestContext.Current.CancellationToken);
        Assert.Equal(["Index", "Value"], squares.Columns);
        Assert.Equal(12, squares.Rows.Count);
        Assert.Equal(["11", "144"], squares.Rows[^1]);

        // An array of objects: their members are columns, and the value is the [DebuggerDisplay] string.
        var orders = await CollectionVisualizers.GridAsync(_debug, "orders", TypeOf("orders"), TestContext.Current.CancellationToken);
        Assert.Contains("Id", orders.Columns);
        Assert.Contains("Customer", orders.Columns);
        Assert.DoesNotContain("_version", orders.Columns);
        Assert.Equal("Order 8: Grace (0 lines)", orders.Rows[1][1]);
        Assert.Equal("8", orders.Rows[1][orders.Columns.ToList().IndexOf("Id")]);
        var table = CollectionVisualizers.Format(orders, "orders");
        Assert.Contains("orders (2 elements)", table, StringComparison.Ordinal);
        Assert.Contains("Grace", table, StringComparison.Ordinal);

        // A byte array holding a PNG.
        Assert.True(CollectionVisualizers.IsByteArray(TypeOf("pixel")), TypeOf("pixel"));
        var bytes = await CollectionVisualizers.BytesAsync(_debug, "pixel", TestContext.Current.CancellationToken);
        Assert.Equal(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="), bytes);
        Assert.Equal(".png", CollectionVisualizers.ImageExtension(bytes));
        // netcoredbg calls Convert itself (not the element-by-element fallback).
        Assert.StartsWith("\"iVBOR", (await _debug.EvaluateAsync("System.Convert.ToBase64String(pixel)", "clipboard", TestContext.Current.CancellationToken)).Result, StringComparison.Ordinal);
        await _debug.StopAsync();
    }

    [Theory]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9' }, ".gif")]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0 }, ".bmp")]
    [InlineData(new byte[] { 1, 2, 3 }, null)]
    [InlineData(new byte[0], null)]
    public void Images_are_recognized_by_their_header(byte[] bytes, string? extension)
    {
        Assert.Equal(extension, CollectionVisualizers.ImageExtension(bytes));
    }

    [Theory]
    [InlineData("int[]", true)]
    [InlineData("Debuggee.Order[]", true)]
    [InlineData("int[,]", true)]
    [InlineData("System.Collections.Generic.List<int>", true)]
    [InlineData("System.Collections.Generic.Dictionary<string, int>", false)]
    [InlineData("string", false)]
    [InlineData(null, false)]
    public void Arrays_and_common_collections_get_a_grid(string? type, bool expected)
    {
        Assert.Equal(expected, CollectionVisualizers.IsCollection(type));
    }

    [Fact]
    public void A_grid_formats_as_an_aligned_table()
    {
        var grid = new DebugGrid(["Index", "Value", "Name"], [["0", "{A}", "Ada"], ["10", "{B}", "Grace Hopper"]], Truncated: true);
        var lines = CollectionVisualizers.Format(grid, "people").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("people (the first 2 elements)", lines[0]);
        Assert.Equal("Index │ Value │ Name", lines[1]);
        Assert.StartsWith("──────┼─", lines[2], StringComparison.Ordinal);
        Assert.Equal("10 │ {B}   │ Grace Hopper", lines[4]);
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
