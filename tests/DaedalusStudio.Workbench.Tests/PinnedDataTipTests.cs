using DaedalusStudio.Debugging;
using DaedalusStudio.Workbench.Debugging;

namespace DaedalusStudio.Workbench.Tests;

/// <summary>Pinned data tips (M14b): the store, following edits, and values across pauses.</summary>
public sealed class PinnedDataTipTests
{
    private const string File = "/src/Program.cs";

    [Fact]
    public void Pinning_the_same_expression_on_a_line_twice_keeps_one_tip()
    {
        var store = new PinnedDataTipStore();
        var changes = new List<string?>();
        store.Changed += (_, path) => changes.Add(path);
        var first = store.Pin(File, 10, "order", "{Order}");
        Assert.Same(first, store.Pin(File, 10, "order"));
        store.Pin(File, 12, "order.Id");
        Assert.Equal([10, 12], store.For(File).Select(t => t.Line));
        Assert.Equal("{Order}", first.Value);

        store.SetComment(first, "  the first order  ");
        Assert.Equal("the first order", first.Comment);
        store.SetComment(first, " ");
        Assert.Null(first.Comment);

        store.Unpin(first);
        Assert.Equal(["order.Id"], store.All.Select(t => t.Expression));
        store.Clear();
        Assert.Empty(store.All);
        Assert.Equal([File, File, File, File, File, null], changes);
    }

    [Fact]
    public void Tips_follow_their_lines_and_go_with_deleted_ones()
    {
        var store = new PinnedDataTipStore();
        store.Pin(File, 3, "a");
        store.Pin(File, 8, "b");
        store.Pin("/src/Other.cs", 3, "c");
        store.Relocate(File, line => line == 3 ? null : line + 2);
        Assert.Equal(["b@10"], store.For(File).Select(t => $"{t.Expression}@{t.Line}"));
        Assert.Equal(3, store.For("/src/Other.cs").Single().Line);
    }

    [Fact]
    public async Task A_value_that_differs_from_the_last_pause_is_marked_changed()
    {
        var store = new PinnedDataTipStore();
        store.Load([(File, 4, "sum", "running total"), (File, 4, "missing", null)]);
        var values = new Queue<string>(["0", "0", "3"]);
        Task<EvaluateResponse> Evaluate(string expression, CancellationToken _) => expression == "sum"
            ? Task.FromResult(new EvaluateResponse(values.Dequeue()))
            : Task.FromException<EvaluateResponse>(new DapException("evaluate", "error CS0103: The name 'missing' does not exist in the current context"));

        await store.RefreshAsync(Evaluate, TestContext.Current.CancellationToken);
        var sum = store.All.Single(t => t.Expression == "sum");
        var missing = store.All.Single(t => t.Expression == "missing");
        Assert.Equal(("0", false), (sum.Value, sum.Changed));
        Assert.Equal("running total", sum.Comment);
        Assert.True(missing.IsError);
        Assert.Contains("CS0103", missing.Value, StringComparison.Ordinal);

        await store.RefreshAsync(Evaluate, TestContext.Current.CancellationToken);
        Assert.False(sum.Changed);
        await store.RefreshAsync(Evaluate, TestContext.Current.CancellationToken);
        Assert.Equal(("3", true), (sum.Value, sum.Changed));
        Assert.False(missing.Changed); // errors are never "changed"
    }
}
