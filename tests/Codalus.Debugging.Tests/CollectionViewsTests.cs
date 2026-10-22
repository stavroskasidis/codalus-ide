using Codalus.Workbench.Debugging;

namespace Codalus.Debugging.Tests;

/// <summary>
/// The shell's collection views (D44, decision 1, A4, A8) on each runtime's answers (<c>Recorded/</c>): netcoredbg's on
/// .NET 6 to 11, recorded from the Debuggee's <c>--datatip</c> scenario, and the .NET Framework and Mono engines'
/// written from the reference source's fields. Each view finds its elements by its field table, skips a dictionary's or
/// a set's free slot, reads a queue from its head and a stack from its top, and gives up on fields it doesn't know.
/// </summary>
public sealed class CollectionViewsTests
{
    public static TheoryData<string> Runtimes => [.. RecordedVariables.Names];

    private static async Task<CollectionView?> ViewAsync(RecordedVariables recorded, string local)
    {
        var variable = recorded.Local(local);
        var kind = CollectionViews.KindOf(variable.Type);
        Assert.NotNull(kind);
        return await CollectionViews.ReadAsync(kind.Value, await recorded.VariablesAsync(variable.VariablesReference), recorded.VariablesAsync, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Every_runtime_has_its_recording()
    {
        Assert.Equal(["datatip-mono", "datatip-net10.0", "datatip-net11.0", "datatip-net48", "datatip-net6.0", "datatip-net7.0", "datatip-net8.0", "datatip-net9.0"], RecordedVariables.Names);
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task A_lists_elements_are_its_items_up_to_its_size(string runtime)
    {
        var recorded = RecordedVariables.Read(runtime);
        var view = await ViewAsync(recorded, "forecasts");
        Assert.NotNull(view);
        Assert.Equal(".NET Core", view.Runtime); // _items and _size on every runtime: the first table reads them
        Assert.Equal(3, view.Count);
        Assert.Equal(["[0]", "[1]", "[2]"], view.Elements.Select(e => e.Variable.Name)); // not the empty fourth slot
        Assert.All(view.Elements, e => Assert.Equal(("{Debuggee.Forecast}", MemberKind.Element), (e.Variable.Value, e.Kind)));
        Assert.All(view.Elements, e => Assert.True(e.Container > 0)); // set in _items, where the slot has the same name
        Assert.False(view.Truncated);
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task A_dictionarys_entries_are_named_by_their_keys_and_its_free_slot_is_skipped(string runtime)
    {
        var recorded = RecordedVariables.Read(runtime);
        var view = await ViewAsync(recorded, "properties");
        Assert.NotNull(view);
        Assert.Equal(TableOf(recorded), view.Runtime);
        Assert.Equal(3, view.Count);
        Assert.Equal(["[\"server.Features\"]", "[\"application.Services\"]", "[\"__MiddlewareDescriptions\"]"], view.Elements.Select(e => e.Variable.Name));
        Assert.Equal("\"services\"", view.Elements[1].Variable.Value);
        Assert.StartsWith("{System.Collections.Generic.List<", view.Elements[2].Variable.Value, StringComparison.Ordinal);
        var entry = view.Elements[0];
        Assert.Equal(["Key", "Value"], entry.Children!.Select(c => c.Variable.Name));
        Assert.Equal("\"server.Features\"", entry.Children![0].Variable.Value);
        Assert.Equal(DebugMember.ByExpression, entry.Container); // no container names it: set by its evaluate name
        if (recorded.Runtime != "mono")
        {
            Assert.EndsWith(".value", entry.Variable.EvaluateName, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task A_sets_elements_skip_its_free_slot(string runtime)
    {
        var recorded = RecordedVariables.Read(runtime);
        var view = await ViewAsync(recorded, "ids");
        Assert.NotNull(view);
        Assert.Equal(3, view.Count);
        Assert.Equal(["3", "8", "13"], view.Elements.Select(e => e.Variable.Value));
        Assert.Equal(["[0]", "[1]", "[2]"], view.Elements.Select(e => e.Variable.Name));
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task A_queue_reads_from_its_head_and_a_stack_from_its_top(string runtime)
    {
        var recorded = RecordedVariables.Read(runtime);
        var queue = await ViewAsync(recorded, "queue");
        Assert.NotNull(queue);
        Assert.Equal(["\"third\"", "\"fourth\"", "\"fifth\""], queue.Elements.Select(e => e.Variable.Value)); // wrapped around the array
        Assert.All(queue.Elements, e => Assert.Equal(DebugMember.ByExpression, e.Container));
        var stack = await ViewAsync(recorded, "stack");
        Assert.NotNull(stack);
        Assert.Equal(["3", "2", "1"], stack.Elements.Select(e => e.Variable.Value));
        Assert.Equal(3, stack.Count);
    }

    [Fact]
    public async Task Fields_no_table_knows_leave_the_raw_members()
    {
        // A List whose fields are named otherwise (another runtime's): no view, the members as they are.
        IReadOnlyList<Variable> raw = [new("items", "{int[4]}", "int[]", 2, "list.items"), new("size", "2", "int", 0, "list.size")];
        Assert.Null(await CollectionViews.ReadAsync(CollectionKind.List, raw, _ => Task.FromResult<IReadOnlyList<Variable>>([]), TestContext.Current.CancellationToken));

        // A dictionary whose entries lack a key: not a table's.
        var entries = new Dictionary<int, IReadOnlyList<Variable>>
        {
            [2] = [new("[0]", "{Entry}", "Entry", 3, "d._entries[0]")],
            [3] = [new("hashCode", "1", "int"), new("next", "-1", "int"), new("value", "1", "int")],
        };
        IReadOnlyList<Variable> dictionary = [new("_entries", "{Entry[1]}", "Entry[]", 2, "d._entries"), new("_count", "1", "int")];
        Assert.Null(await CollectionViews.ReadAsync(CollectionKind.Dictionary, dictionary, r => Task.FromResult(entries[r]), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("System.Collections.Generic.List<int>", CollectionKind.List)]
    [InlineData("System.Collections.Generic.Dictionary<string, object>", CollectionKind.Dictionary)]
    [InlineData("System.Collections.Generic.HashSet<System.Int32>", CollectionKind.HashSet)]
    [InlineData("System.Collections.Generic.Queue<string>", CollectionKind.Queue)]
    [InlineData("System.Collections.Generic.Stack<int>", CollectionKind.Stack)]
    [InlineData("System.Collections.Generic.List`1", CollectionKind.List)]
    [InlineData("List<int>", CollectionKind.List)]
    [InlineData("System.Collections.Generic.Dictionary<string, object>.KeyCollection", null)]
    [InlineData("System.Collections.Generic.SortedList<string, int>", null)]
    [InlineData("App.List<int>", null)]
    [InlineData("int[]", null)]
    [InlineData(null, null)]
    public void The_views_are_chosen_by_the_type_name(string? type, CollectionKind? expected) => Assert.Equal(expected, CollectionViews.KindOf(type));

    /// <summary>The field table each runtime's collections match.</summary>
    private static string TableOf(RecordedVariables recorded) => recorded.Runtime is "net48" or "mono" ? ".NET Framework" : ".NET Core";
}
