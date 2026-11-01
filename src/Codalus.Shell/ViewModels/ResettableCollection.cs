using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Codalus.Shell.ViewModels;

/// <summary>
/// An observable collection whose contents can be replaced at once, with one <see cref="NotifyCollectionChangedAction.Reset"/>:
/// replacing a large list an item at a time raises a change, and a view update, for each.
/// </summary>
public sealed class ResettableCollection<T> : ObservableCollection<T>
{
    /// <summary>Replaces the contents with <paramref name="items"/>.</summary>
    public void Reset(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        if (Items is List<T> list)
        {
            list.AddRange(items);
        }
        else
        {
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Inserts <paramref name="items"/> at <paramref name="index"/> with one <see cref="NotifyCollectionChangedAction.Add"/>.
    /// A list that keeps its selection's anchor updates it once, not once per item: Avalonia's queued one scroll to the
    /// selected row for each row inserted above it, which walked the Solution Explorer through them (M56).
    /// </summary>
    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        var added = items as List<T> ?? [.. items];
        ((List<T>)Items).InsertRange(index, added);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, index));
    }

    /// <summary>
    /// Shows <paramref name="items"/> in place: the items equal at the start and at the end stay, and the rest is replaced
    /// with one removal and one insertion (items streaming in at the end: one insertion), or with one reset when none
    /// stays. A list brought up to date an item at a time raised a change, and a list box's update, for each: tens of
    /// thousands of references or Find in Files results paused the window at each show and each keystroke in its search (M56).
    /// </summary>
    public void Patch(IReadOnlyList<T> items, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        Patch(items, (row, item) => comparer.Equals(row, item), item => item);
    }

    /// <summary>
    /// Shows the rows of <paramref name="items"/> in place, as <see cref="Patch(IReadOnlyList{T}, IEqualityComparer{T})"/>:
    /// the rows <paramref name="alike"/> their items at the start and at the end stay, and <paramref name="update"/> takes
    /// their new items; the rest are <paramref name="create"/>d.
    /// </summary>
    public void Patch<TItem>(IReadOnlyList<TItem> items, Func<T, TItem, bool> alike, Func<TItem, T> create, Action<T, TItem>? update = null)
    {
        var prefix = 0;
        while (prefix < items.Count && prefix < Count && alike(Items[prefix], items[prefix]))
        {
            update?.Invoke(Items[prefix], items[prefix]);
            prefix++;
        }

        var suffix = 0;
        while (suffix < items.Count - prefix && suffix < Count - prefix && alike(Items[Count - 1 - suffix], items[items.Count - 1 - suffix]))
        {
            update?.Invoke(Items[Count - 1 - suffix], items[items.Count - 1 - suffix]);
            suffix++;
        }

        if (prefix == 0 && suffix == 0)
        {
            if (Count > 0 || items.Count > 0)
            {
                Reset(items.Select(create));
            }

            return;
        }

        RemoveRange(prefix, Count - suffix - prefix);
        InsertRange(prefix, [.. items.Skip(prefix).Take(items.Count - suffix - prefix).Select(create)]);
    }

    /// <summary>Removes <paramref name="count"/> items from <paramref name="index"/> with one <see cref="NotifyCollectionChangedAction.Remove"/>.</summary>
    public void RemoveRange(int index, int count)
    {
        if (count == 0)
        {
            return;
        }

        CheckReentrancy();
        var list = (List<T>)Items;
        var removed = list.GetRange(index, count);
        list.RemoveRange(index, count);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }
}
