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
}
