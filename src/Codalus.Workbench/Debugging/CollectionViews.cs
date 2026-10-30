using System.Globalization;
using Codalus.Debugging;

namespace Codalus.Workbench.Debugging;

/// <summary>The collections the shell shows as their elements (D44, decision 1).</summary>
public enum CollectionKind
{
    List,
    Dictionary,
    HashSet,
    Queue,
    Stack,
}

/// <summary>A collection's elements as a view reads them, and how many there are.</summary>
/// <param name="Elements">The element rows, in the collection's order (a stack's top first, a queue's head first).</param>
/// <param name="Count">The collection's count.</param>
/// <param name="Runtime">The field table that matched (<c>.NET Core 3+</c>, <c>.NET Framework</c>…).</param>
public sealed record CollectionView(IReadOnlyList<DebugMember> Elements, int Count, string Runtime)
{
    /// <summary>Elements past <see cref="CollectionViews.MaxElements"/> aren't read.</summary>
    public bool Truncated => Elements.Count < Count;
}

/// <summary>
/// The shell's own views of List, Dictionary, HashSet, Queue and Stack (D44, decision 1, A4, A8): netcoredbg 3.2 can't
/// build their <c>[DebuggerTypeProxy]</c> (it has no <c>new</c> or <c>typeof</c>, and matches arguments by exact type),
/// but it reads private fields and array elements, so a view reads the elements from the collection's fields, as the
/// sample Dictionary Visualizer reads <c>_entries</c>. Field names differ between runtimes, so each view probes the
/// names of its field table in order (<see cref="Tables"/>) and gives up (null: the raw members are shown) when one is
/// missing. The .NET Framework and Mono engines list the same fields by the same names, so the views work there too.
/// </summary>
/// <remarks>
/// TODO(issue): [DebuggerTypeProxy] in general (section P of docs/design/controls/DataTip.dc.html) waits for netcoredbg
/// to construct a proxy: proposed upstream (Samsung/netcoredbg) first.
/// TODO(issue): Results View (enumerating any IEnumerable) waits for netcoredbg to bind Enumerable.ToArray.
/// </remarks>
public static class CollectionViews
{
    /// <summary>The most elements a view reads; the rest are counted, not shown.</summary>
    public const int MaxElements = 1000;

    /// <summary>
    /// Where each runtime keeps a collection's elements. A view tries each table's names in order: the first whose
    /// fields are all there reads the elements.
    /// </summary>
    public static readonly IReadOnlyList<FieldTable> Tables =
    [
        // .NET 5 to 11 (net6 to net11 recorded) and .NET Core 3: List _items/_size; Dictionary _entries/_count with
        // key/value/next (a free slot's next is below -1); HashSet _entries/_count with Value/Next (net5+);
        // Queue _array/_head/_size; Stack _array/_size.
        new(".NET Core", "_items", "_size", "_entries", "_count", "key", "value", "next", "hashCode", "_entries", "_count", "Value", "Next", "HashCode", "_array", "_head", "_size"),
        // .NET Core 2 and 3's HashSet: _slots up to _lastIndex, a free slot's hashCode below 0.
        new(".NET Core 2-3", "_items", "_size", "_entries", "_count", "key", "value", "next", "hashCode", "_slots", "_lastIndex", "value", "next", "hashCode", "_array", "_head", "_size"),
        // .NET Framework 4.8 and Mono (the reference source's): Dictionary entries/count, a free slot's hashCode -1;
        // HashSet m_slots up to m_lastIndex.
        new(".NET Framework", "_items", "_size", "entries", "count", "key", "value", "next", "hashCode", "m_slots", "m_lastIndex", "value", "next", "hashCode", "_array", "_head", "_size"),
    ];

    /// <summary>The view for a type name (netcoredbg's <c>System.Collections.Generic.List&lt;int&gt;</c>, a metadata name's <c>List`1</c>), or null.</summary>
    public static CollectionKind? KindOf(string? type)
    {
        if (string.IsNullOrEmpty(type) || type.EndsWith(']'))
        {
            return null;
        }

        var generic = type.IndexOfAny(['<', '`']);
        if (generic < 0)
        {
            return null;
        }

        var name = type[..generic];
        if (name.StartsWith("System.Collections.Generic.", StringComparison.Ordinal))
        {
            name = name["System.Collections.Generic.".Length..];
        }

        // A nested type (Dictionary<K, V>.KeyCollection) isn't the collection.
        var close = type.LastIndexOf('>');
        if (close >= 0 && close < type.Length - 1)
        {
            return null;
        }

        return name switch
        {
            "List" => CollectionKind.List,
            "Dictionary" => CollectionKind.Dictionary,
            "HashSet" => CollectionKind.HashSet,
            "Queue" => CollectionKind.Queue,
            "Stack" => CollectionKind.Stack,
            _ => null,
        };
    }

    /// <summary>
    /// The elements of a collection of <paramref name="kind"/>, read from its members (<paramref name="raw"/>, the
    /// adapter's) through <paramref name="variables"/>; null when its fields aren't any table's (the raw members then).
    /// </summary>
    public static Task<CollectionView?> ReadAsync(CollectionKind kind, IReadOnlyList<Variable> raw, Func<int, Task<IReadOnlyList<Variable>>> variables, CancellationToken cancellationToken = default) =>
        ReadAsync(kind, raw, variables, null, cancellationToken);

    /// <summary>
    /// The elements of a collection; with <paramref name="page"/> (a reference, the first slot, how many), the slots of an
    /// array the adapter counts are read a page at a time, only those the view shows (M56): a list of 100,000 read all
    /// 131,072 of its slots to keep 1,000.
    /// </summary>
    public static async Task<CollectionView?> ReadAsync(CollectionKind kind, IReadOnlyList<Variable> raw, Func<int, Task<IReadOnlyList<Variable>>> variables, Func<int, int, int, Task<IReadOnlyList<Variable>>>? page, CancellationToken cancellationToken = default)
    {
        var slots = new Slots(variables, page);
        foreach (var table in Tables)
        {
            var view = kind switch
            {
                CollectionKind.List => await ListAsync(table, raw, slots).ConfigureAwait(false),
                CollectionKind.Queue => await QueueAsync(table, raw, slots).ConfigureAwait(false),
                CollectionKind.Stack => await StackAsync(table, raw, slots).ConfigureAwait(false),
                CollectionKind.Dictionary => await EntriesAsync(table, raw, slots, variables, table.DictionaryEntries, table.DictionaryCount, dictionary: true, cancellationToken).ConfigureAwait(false),
                _ => await EntriesAsync(table, raw, slots, variables, table.SetEntries, table.SetCount, dictionary: false, cancellationToken).ConfigureAwait(false),
            };
            if (view is not null)
            {
                return view;
            }
        }

        return null;
    }

    private static Variable? Field(IReadOnlyList<Variable> members, string name) => members.FirstOrDefault(m => m.Name == name);

    private static int? Int(IReadOnlyList<Variable> members, string name) =>
        Field(members, name) is { } field && int.TryParse(field.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>
    /// A collection's backing array (<paramref name="name"/>): its slots from <c>start</c>, <c>count</c> of them (fewer
    /// past its end), read a page at a time when the adapter counts its elements and pages them, else all of it once.
    /// </summary>
    private sealed class Slots(Func<int, Task<IReadOnlyList<Variable>>> variables, Func<int, int, int, Task<IReadOnlyList<Variable>>>? page)
    {
        private readonly Dictionary<int, IReadOnlyList<Variable>> _whole = [];

        /// <summary>The array's length as the adapter counts it (netcoredbg's named children, DAP's indexed ones), or null.</summary>
        private static int? Length(Variable array) => array.IndexedVariables is > 0 ? array.IndexedVariables : array.NamedVariables;

        /// <summary>How many slots the array has.</summary>
        public async Task<int> LengthAsync(Variable array) =>
            page is not null && Length(array) is { } length ? length : (await WholeAsync(array).ConfigureAwait(false)).Count;

        public async Task<IReadOnlyList<Variable>> ReadAsync(Variable array, int start, int count)
        {
            if (count <= 0 || start < 0)
            {
                return [];
            }

            if (page is not null && Length(array) is { } length)
            {
                var take = Math.Min(count, length - start);
                if (take <= 0)
                {
                    return [];
                }

                // An adapter that sends the whole array anyway: the page's own slots.
                var slots = (await page(array.VariablesReference, start, take).ConfigureAwait(false)).Where(e => e.Name.StartsWith('[')).ToList();
                return slots.Count > take ? [.. slots.Skip(slots.Count >= start + take ? start : 0).Take(take)] : slots;
            }

            return [.. (await WholeAsync(array).ConfigureAwait(false)).Skip(start).Take(count)];
        }

        private async Task<IReadOnlyList<Variable>> WholeAsync(Variable array)
        {
            if (!_whole.TryGetValue(array.VariablesReference, out var all))
            {
                all = [.. (await variables(array.VariablesReference).ConfigureAwait(false)).Where(e => e.Name.StartsWith('['))];
                _whole[array.VariablesReference] = all;
            }

            return all;
        }
    }

    /// <summary>The backing array <paramref name="name"/>: its variable (null: a null array), or null when the field isn't there.</summary>
    private static (Variable? Array, bool Found) ArrayField(IReadOnlyList<Variable> members, string name)
    {
        if (Field(members, name) is not { } array)
        {
            return (null, false);
        }

        if (array.VariablesReference <= 0)
        {
            return array.Value == "null" ? (null, true) : (null, false);
        }

        return (array, true);
    }

    /// <summary>
    /// An element of the view: <c>[index]</c>, the slot's value; set in its array (<paramref name="array"/>, when the
    /// slot's name is the index) or by its evaluate name (a queue's or a stack's slots are in another order).
    /// </summary>
    private static DebugMember Element(Variable slot, int index, int? array) =>
        new(slot with { Name = "[" + index.ToString(CultureInfo.InvariantCulture) + "]" }, MemberKind.Element) { Container = array ?? DebugMember.ByExpression };

    // _items up to _size.
    private static async Task<CollectionView?> ListAsync(FieldTable table, IReadOnlyList<Variable> raw, Slots slots)
    {
        if (Int(raw, table.ListSize) is not { } size || ArrayField(raw, table.ListItems) is not { Found: true } found)
        {
            return null;
        }

        if (found.Array is not { } array)
        {
            return new CollectionView([], size, table.Runtime);
        }

        var items = await slots.ReadAsync(array, 0, Math.Min(size, MaxElements)).ConfigureAwait(false);
        return new CollectionView([.. items.Select((slot, i) => Element(slot, i, array.VariablesReference))], size, table.Runtime);
    }

    // _array from _head, wrapping, _size of them.
    private static async Task<CollectionView?> QueueAsync(FieldTable table, IReadOnlyList<Variable> raw, Slots slots)
    {
        if (Int(raw, table.QueueSize) is not { } size || Int(raw, table.QueueHead) is not { } head || ArrayField(raw, table.QueueArray) is not { Found: true } found)
        {
            return null;
        }

        if (found.Array is not { } array || await slots.LengthAsync(array).ConfigureAwait(false) is not (> 0 and var length))
        {
            return new CollectionView([], size, table.Runtime);
        }

        // From the head to the array's end, then from its start: two pages at most.
        var shown = Math.Min(size, MaxElements);
        var from = head % length;
        var first = await slots.ReadAsync(array, from, Math.Min(shown, length - from)).ConfigureAwait(false);
        var rest = shown > first.Count ? await slots.ReadAsync(array, 0, shown - first.Count).ConfigureAwait(false) : [];
        return new CollectionView([.. first.Concat(rest).Select((slot, i) => Element(slot, i, null))], size, table.Runtime);
    }

    // _array up to _size, the top (the last) first.
    private static async Task<CollectionView?> StackAsync(FieldTable table, IReadOnlyList<Variable> raw, Slots slots)
    {
        if (Field(raw, table.QueueHead) is not null || Int(raw, table.QueueSize) is not { } size || ArrayField(raw, table.QueueArray) is not { Found: true } found)
        {
            return null; // a queue's fields are a stack's and _head: that's not a stack
        }

        if (found.Array is not { } array)
        {
            return new CollectionView([], size, table.Runtime);
        }

        // The top 1,000: the last slots in use, one page.
        var shown = Math.Min(size, MaxElements);
        var top = await slots.ReadAsync(array, size - shown, shown).ConfigureAwait(false);
        return new CollectionView([.. top.Reverse().Select((slot, i) => Element(slot, i, null))], size, table.Runtime);
    }

    // A dictionary's or a set's slots up to the bound (the count of slots used so far), the free ones skipped.
    private static async Task<CollectionView?> EntriesAsync(FieldTable table, IReadOnlyList<Variable> raw, Slots slots, Func<int, Task<IReadOnlyList<Variable>>> variables, string entriesName, string boundName, bool dictionary, CancellationToken cancellationToken)
    {
        if (Int(raw, boundName) is not { } bound || ArrayField(raw, entriesName) is not { Found: true } found)
        {
            return null;
        }

        var (key, value, next, hash) = dictionary
            ? (table.DictionaryKey, table.DictionaryValue, table.DictionaryNext, table.DictionaryHashCode)
            : (string.Empty, table.SetValue, table.SetNext, table.SetHashCode);
        var elements = new List<DebugMember>();
        var end = found.Array is { } array ? Math.Min(bound, await slots.LengthAsync(array).ConfigureAwait(false)) : 0;
        IReadOnlyList<Variable> entries = [];
        var first = 0; // the slot entries[0] is
        var i = 0;
        for (; i < end && elements.Count < MaxElements; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (i >= first + entries.Count)
            {
                // The next page of slots, until 1,000 used ones are read or the bound is.
                first = i;
                entries = await slots.ReadAsync(found.Array!, i, Math.Min(MaxElements, end - i)).ConfigureAwait(false);
                if (entries.Count == 0)
                {
                    break;
                }
            }

            var entry = entries[i - first];
            if (entry.VariablesReference <= 0)
            {
                return null;
            }

            var fields = await variables(entry.VariablesReference).ConfigureAwait(false);
            if (Field(fields, value) is not { } item || (dictionary && Field(fields, key) is null))
            {
                return null; // not this table's entry
            }

            // A free slot: .NET Core 3+ chains free slots below -1 in next; .NET Framework, Mono and .NET Core 2 mark them
            // with a hash code of -1 (a set's free slot, any negative hash code).
            if (Int(fields, next) is < -1 || (Int(fields, hash) is { } code && (dictionary ? code == -1 : code < 0)))
            {
                continue;
            }

            if (!dictionary)
            {
                elements.Add(Element(item, elements.Count, null));
                continue;
            }

            var keyField = Field(fields, key)!;
            elements.Add(new DebugMember(item with { Name = "[" + keyField.Value + "]" }, MemberKind.Element)
            {
                Container = DebugMember.ByExpression,
                Children =
                [
                    new DebugMember(keyField with { Name = "Key" }, MemberKind.Field) { Container = DebugMember.ByExpression },
                    new DebugMember(item with { Name = "Value" }, MemberKind.Field) { Container = DebugMember.ByExpression },
                ],
            });
        }

        // Every slot read: the used ones are the count; stopped at the limit, the bound less the free slots.
        var count = i >= end ? elements.Count : bound - FreeCount(raw);
        return new CollectionView(elements, count, table.Runtime);
    }

    /// <summary>A dictionary's or a set's free slots (<c>_freeCount</c>, <c>freeCount</c>, <c>m_freeCount</c>): its count is the used slots less them.</summary>
    private static int FreeCount(IReadOnlyList<Variable> raw) => Int(raw, "_freeCount") ?? Int(raw, "freeCount") ?? Int(raw, "m_freeCount") ?? 0;
}

/// <summary>
/// One runtime's field names for the collection views (D44): List's items and size; Dictionary's entries, count and an
/// entry's key, value, next and hash code; HashSet's entries (or slots), count (or last index) and an entry's value, next
/// and hash code; Queue's and Stack's array, head and size.
/// </summary>
public sealed record FieldTable(
    string Runtime,
    string ListItems,
    string ListSize,
    string DictionaryEntries,
    string DictionaryCount,
    string DictionaryKey,
    string DictionaryValue,
    string DictionaryNext,
    string DictionaryHashCode,
    string SetEntries,
    string SetCount,
    string SetValue,
    string SetNext,
    string SetHashCode,
    string QueueArray,
    string QueueHead,
    string QueueSize);
