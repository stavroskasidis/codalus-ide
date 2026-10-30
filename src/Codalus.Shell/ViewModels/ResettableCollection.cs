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
