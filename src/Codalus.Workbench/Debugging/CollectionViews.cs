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
    public static async Task<CollectionView?> ReadAsync(CollectionKind kind, IReadOnlyList<Variable> raw, Func<int, Task<IReadOnlyList<Variable>>> variables, CancellationToken cancellationToken = default)
    {
        foreach (var table in Tables)
        {
            var view = kind switch
            {
                CollectionKind.List => await ListAsync(table, raw, variables).ConfigureAwait(false),
                CollectionKind.Queue => await QueueAsync(table, raw, variables).ConfigureAwait(false),
                CollectionKind.Stack => await StackAsync(table, raw, variables).ConfigureAwait(false),
                CollectionKind.Dictionary => await EntriesAsync(table, raw, variables, table.DictionaryEntries, table.DictionaryCount, dictionary: true, cancellationToken).ConfigureAwait(false),
                _ => await EntriesAsync(table, raw, variables, table.SetEntries, table.SetCount, dictionary: false, cancellationToken).ConfigureAwait(false),
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

    private static async Task<(IReadOnlyList<Variable> Slots, int Reference)?> ArrayAsync(IReadOnlyList<Variable> members, string name, Func<int, Task<IReadOnlyList<Variable>>> variables)
    {
        if (Field(members, name) is not { } array)
        {
            return null;
        }

        if (array.VariablesReference <= 0)
        {
            return array.Value == "null" ? ([], 0) : null;
        }

        return ([.. (await variables(array.VariablesReference).ConfigureAwait(false)).Where(e => e.Name.StartsWith('['))], array.VariablesReference);
    }

    /// <summary>
    /// An element of the view: <c>[index]</c>, the slot's value; set in its array (<paramref name="array"/>, when the
    /// slot's name is the index) or by its evaluate name (a queue's or a stack's slots are in another order).
    /// </summary>
    private static DebugMember Element(Variable slot, int index, int? array) =>
        new(slot with { Name = "[" + index.ToString(CultureInfo.InvariantCulture) + "]" }, MemberKind.Element) { Container = array ?? DebugMember.ByExpression };

    // _items up to _size.
    private static async Task<CollectionView?> ListAsync(FieldTable table, IReadOnlyList<Variable> raw, Func<int, Task<IReadOnlyList<Variable>>> variables)
    {
        if (Int(raw, table.ListSize) is not { } size || await ArrayAsync(raw, table.ListItems, variables).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        var (items, reference) = found;

        var shown = Math.Min(Math.Min(size, items.Count), MaxElements);
        return new CollectionView([.. items.Take(shown).Select((slot, i) => Element(slot, i, reference))], size, table.Runtime);
    }

    // _array from _head, wrapping, _size of them.
    private static async Task<CollectionView?> QueueAsync(FieldTable table, IReadOnlyList<Variable> raw, Func<int, Task<IReadOnlyList<Variable>>> variables)
    {
        if (Int(raw, table.QueueSize) is not { } size || Int(raw, table.QueueHead) is not { } head || await ArrayAsync(raw, table.QueueArray, variables).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        var array = found.Slots;

        var elements = new List<DebugMember>();
        for (var i = 0; i < size && i < MaxElements && array.Count > 0; i++)
        {
            elements.Add(Element(array[(head + i) % array.Count], i, null));
        }

        return new CollectionView(elements, size, table.Runtime);
    }

    // _array up to _size, the top (the last) first.
    private static async Task<CollectionView?> StackAsync(FieldTable table, IReadOnlyList<Variable> raw, Func<int, Task<IReadOnlyList<Variable>>> variables)
    {
        if (Field(raw, table.QueueHead) is not null || Int(raw, table.QueueSize) is not { } size || await ArrayAsync(raw, table.QueueArray, variables).ConfigureAwait(false) is not { } found)
        {
            return null; // a queue's fields are a stack's and _head: that's not a stack
        }

        var array = found.Slots;

        var elements = new List<DebugMember>();
        for (var i = 0; i < size && i < MaxElements && size - 1 - i < array.Count; i++)
        {
            elements.Add(Element(array[size - 1 - i], i, null));
        }

        return new CollectionView(elements, size, table.Runtime);
    }

    // A dictionary's or a set's slots up to the bound (the count of slots used so far), the free ones skipped.
    private static async Task<CollectionView?> EntriesAsync(FieldTable table, IReadOnlyList<Variable> raw, Func<int, Task<IReadOnlyList<Variable>>> variables, string entriesName, string boundName, bool dictionary, CancellationToken cancellationToken)
    {
        if (Int(raw, boundName) is not { } bound || await ArrayAsync(raw, entriesName, variables).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        var entries = found.Slots;

        var (key, value, next, hash) = dictionary
            ? (table.DictionaryKey, table.DictionaryValue, table.DictionaryNext, table.DictionaryHashCode)
            : (string.Empty, table.SetValue, table.SetNext, table.SetHashCode);
        var elements = new List<DebugMember>();
        var end = Math.Min(bound, entries.Count);
        var i = 0;
        for (; i < end && elements.Count < MaxElements; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries[i].VariablesReference <= 0)
            {
                return null;
            }

            var fields = await variables(entries[i].VariablesReference).ConfigureAwait(false);
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
