using System.Reflection;
using System.Text.Json;
using Codalus.Debugging;

namespace Codalus.Workbench.Tests;

/// <summary>
/// netcoredbg answers a request that runs past its fixed 15 seconds with "Command execution timed out." while the
/// request goes on (a session's first evaluation on a loaded machine, PR #35's Windows CI). An expression that only
/// reads is asked once more; any other isn't, since it could do what it does twice. The adapter is the scripted one
/// (<c>Codalus.LspConformance.Server --dap</c>), whose first evaluations time out (<c>evaluateTimesOut</c>).
/// </summary>
public sealed class EvaluateTimeoutTests
{
    private static string Metadata(string key) =>
        typeof(EvaluateTimeoutTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;

    private static string ScriptedAdapter => Path.Combine(Metadata("CodalusRepoRoot"), "tests", "Codalus.LspConformance.Server", "bin", Metadata("CodalusConfiguration"), Codalus.Tests.RepoBuild.TargetFramework, "codalus-lsp-conformance.dll");

    private static CancellationToken Timeout() =>
        CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token).Token;

    private static async Task<DebugAdapter> StartAsync(int evaluateTimesOut)
    {
        var adapter = await DebugAdapter.StartAsync(new DebugAdapterStartInfo("dotnet", [ScriptedAdapter, "--dap"], AppContext.BaseDirectory), Timeout());
        var launch = JsonSerializer.SerializeToElement(new Dictionary<string, object> { ["evaluateTimesOut"] = evaluateTimesOut });
        await adapter.LaunchAsync(launch, DapJsonContext.Default.JsonElement, Timeout());
        return adapter;
    }

    [Fact]
    public async Task An_expression_that_only_reads_is_asked_again_after_netcoredbgs_command_timeout()
    {
        await using var adapter = await StartAsync(evaluateTimesOut: 1);
        var log = new System.Text.StringBuilder();
        adapter.Log += (_, line) => { lock (log) { log.AppendLine(line); } };

        Assert.Equal("(order).Customer (request 2)", (await adapter.EvaluateAsync("(order).Customer", 1000, "watch", Timeout())).Result);
        lock (log)
        {
            Assert.Contains("The debugger took longer than its 15 seconds to evaluate (order).Customer; asking again.", log.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_call_is_not_asked_again_after_netcoredbgs_command_timeout()
    {
        await using var adapter = await StartAsync(evaluateTimesOut: 1);

        var error = await Assert.ThrowsAsync<DapException>(() => adapter.EvaluateAsync("orders.Add(order)", 1000, "watch", Timeout()));

        Assert.True(DebugAdapter.IsCommandTimeout(error));
        Assert.Equal("orders.Count (request 2)", (await adapter.EvaluateAsync("orders.Count", 1000, "watch", Timeout())).Result); // asked once
    }

    [Theory]
    [InlineData("i", true)]
    [InlineData("order.Lines.Count", true)]
    [InlineData("(order).Customer", true)]
    [InlineData(" ( ( order ) ) . Id ", true)]
    [InlineData("this._items", true)]
    [InlineData("@class", true)]
    [InlineData("Next()", false)]
    [InlineData("(order).Format(1)", false)]
    [InlineData("items[0]", false)]
    [InlineData("i++", false)]
    [InlineData("i = 3", false)]
    [InlineData("a + b", false)]
    [InlineData("new Order()", false)]
    [InlineData("(a)(b)", false)]
    [InlineData("order?.Id", false)]
    [InlineData("", false)]
    public void Only_names_and_their_members_only_read(string expression, bool onlyReads) =>
        Assert.Equal(onlyReads, DebugAdapter.OnlyReads(expression));
}
