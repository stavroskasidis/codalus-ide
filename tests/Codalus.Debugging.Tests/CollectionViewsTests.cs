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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_dictionary_past_a_page_of_slots_reads_page_after_page_until_a_thousand_entries(bool adapterSendsWholeArray)
    {
        // 1,800 slots, every third free: the first 1,000 entries are in the first 1,500 slots, two pages (M56). An adapter
        // that sends the whole array whatever the page gives each page its own slots.
        var adapter = new PagedArrays(adapterSendsWholeArray);
        adapter.Array(2, 1800, i => new Variable($"[{i}]", "{Entry}", "System.Collections.Generic.Dictionary<string, int>.Entry", 10_000 + i, $"d._entries[{i}]"));
        for (var i = 0; i < 1800; i++)
        {
            adapter.Members[10_000 + i] = [new("hashCode", "1", "int"), new("next", i % 3 == 2 ? "-3" : "-1", "int"), new("key", $"\"k{i}\"", "string"), new("value", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "int")];
        }

        IReadOnlyList<Variable> raw = [new("_entries", "{Entry[1800]}", "Entry[]", 2, "d._entries", NamedVariables: 1800), new("_count", "1800", "int")];
        var view = await CollectionViews.ReadAsync(CollectionKind.Dictionary, raw, adapter.VariablesAsync, adapter.PageAsync, TestContext.Current.CancellationToken);
        Assert.NotNull(view);
        Assert.Equal(1000, view.Elements.Count);
        Assert.Equal(("[\"k0\"]", "[\"k1\"]", "[\"k3\"]", "[\"k1498\"]"), (view.Elements[0].Variable.Name, view.Elements[1].Variable.Name, view.Elements[2].Variable.Name, view.Elements[^1].Variable.Name));
        Assert.Equal([(0, 1000), (1000, 800)], adapter.Pages);
        Assert.DoesNotContain(2, adapter.Whole); // never the whole array
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_stack_past_a_page_reads_its_top_page_and_a_set_its_used_slots(bool adapterSendsWholeArray)
    {
        // A stack of 1,500 in 2,048 slots: its top 1,000, slots 500 to 1,499, top first, in one page.
        var adapter = new PagedArrays(adapterSendsWholeArray);
        adapter.Array(3, 2048, i => new Variable($"[{i}]", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "int", 0, $"s._array[{i}]"));
        IReadOnlyList<Variable> stack = [new("_array", "{int[2048]}", "int[]", 3, "s._array", NamedVariables: 2048), new("_size", "1500", "int")];
        var view = await CollectionViews.ReadAsync(CollectionKind.Stack, stack, adapter.VariablesAsync, adapter.PageAsync, TestContext.Current.CancellationToken);
        Assert.NotNull(view);
        Assert.Equal((1000, 1500, true), (view.Elements.Count, view.Count, view.Truncated));
        Assert.Equal(("1499", "500"), (view.Elements[0].Variable.Value, view.Elements[^1].Variable.Value));
        Assert.Equal([(500, 1000)], adapter.Pages);

        // A set of 1,200 slots, the first 300 free: 900 used ones, read in two pages.
        adapter = new PagedArrays(adapterSendsWholeArray);
        adapter.Array(4, 1200, i => new Variable($"[{i}]", "{Entry}", "System.Collections.Generic.HashSet<int>.Entry", 20_000 + i, $"h._entries[{i}]"));
        for (var i = 0; i < 1200; i++)
        {
            adapter.Members[20_000 + i] = [new("HashCode", "1", "int"), new("Next", i < 300 ? "-3" : "-1", "int"), new("Value", i.ToString(System.Globalization.CultureInfo.InvariantCulture), "int")];
        }

        IReadOnlyList<Variable> set = [new("_entries", "{Entry[1200]}", "Entry[]", 4, "h._entries", NamedVariables: 1200), new("_count", "1200", "int")];
        view = await CollectionViews.ReadAsync(CollectionKind.HashSet, set, adapter.VariablesAsync, adapter.PageAsync, TestContext.Current.CancellationToken);
        Assert.NotNull(view);
        Assert.Equal((900, "300", "1199"), (view.Elements.Count, view.Elements[0].Variable.Value, view.Elements[^1].Variable.Value));
        Assert.Equal([(0, 1000), (1000, 200)], adapter.Pages);
    }

    /// <summary>The field table each runtime's collections match.</summary>
    private static string TableOf(RecordedVariables recorded) => recorded.Runtime is "net48" or "mono" ? ".NET Framework" : ".NET Core";

    /// <summary>
    /// An adapter's arrays, made up as they're asked for, paged by start and count as netcoredbg pages them, or sent whole
    /// whatever the page (an adapter that doesn't page); and each one's members.
    /// </summary>
    private sealed class PagedArrays(bool sendsWholeArray)
    {
        private readonly Dictionary<int, Variable[]> _arrays = [];

        public Dictionary<int, IReadOnlyList<Variable>> Members { get; } = [];

        /// <summary>The pages asked for: each one's first slot and count.</summary>
        public List<(int Start, int Count)> Pages { get; } = [];

        /// <summary>The arrays read whole (no page).</summary>
        public List<int> Whole { get; } = [];

        public void Array(int reference, int length, Func<int, Variable> slot) => _arrays[reference] = [.. Enumerable.Range(0, length).Select(slot)];

        public Task<IReadOnlyList<Variable>> VariablesAsync(int reference)
        {
            if (_arrays.TryGetValue(reference, out var array))
            {
                Whole.Add(reference);
                return Task.FromResult<IReadOnlyList<Variable>>(array);
            }

            return Task.FromResult(Members.TryGetValue(reference, out var members) ? members : []);
        }

        public Task<IReadOnlyList<Variable>> PageAsync(int reference, int start, int count)
        {
            Pages.Add((start, count));
            var array = _arrays[reference];
            return Task.FromResult<IReadOnlyList<Variable>>(sendsWholeArray ? array : [.. array.Skip(start).Take(count)]);
        }
    }
}
