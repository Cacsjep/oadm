using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Oadm.Client.Infrastructure;

/// <summary>A list whose whole content can be replaced in one step (one Reset notification).</summary>
public interface IResettableList
{
    /// <summary>Replaces the content with <paramref name="items"/> (items of the wrong type are skipped).</summary>
    void ResetTo(IEnumerable items);
}

/// <summary>
/// <see cref="ObservableCollection{T}"/> with bulk operations that raise a single
/// <see cref="NotifyCollectionChangedAction.Reset"/> instead of one event per item. Scale: adding,
/// filtering or selecting 5,000 rows one by one raises 5,000 events, and every DataGrid, filter and
/// summary bound to the collection reacts to each of them.
/// </summary>
public class RangeObservableCollection<T> : ObservableCollection<T>, IResettableList
{
    public RangeObservableCollection()
    {
    }

    public RangeObservableCollection(IEnumerable<T> items)
        : base(items)
    {
    }

    /// <summary>Replaces the whole content; one Reset. Nothing happens when the content is the same.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items as IList<T> ?? items.ToList();
        if (SameItems(list))
        {
            return;
        }

        CheckReentrancy();
        Items.Clear();
        foreach (var item in list)
        {
            Items.Add(item);
        }

        RaiseReset();
    }

    /// <summary>Appends items; one Add event for a single item, else one Reset.</summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items as IList<T> ?? items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        if (list.Count == 1)
        {
            Add(list[0]);
            return;
        }

        CheckReentrancy();
        foreach (var item in list)
        {
            Items.Add(item);
        }

        RaiseReset();
    }

    /// <summary>Inserts items at <paramref name="index"/> in their order; one Add event for a single item, else one Reset.</summary>
    public void InsertRange(int index, IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var list = items as IList<T> ?? items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        if (list.Count == 1)
        {
            Insert(index, list[0]);
            return;
        }

        CheckReentrancy();
        if (Items is List<T> backing)
        {
            backing.InsertRange(index, list);
        }
        else
        {
            for (var i = 0; i < list.Count; i++)
            {
                Items.Insert(index + i, list[i]);
            }
        }

        RaiseReset();
    }

    /// <summary>Removes every item matching <paramref name="match"/> in O(n); one Remove event for a single item, else one Reset. Returns the number removed.</summary>
    public int RemoveAll(Predicate<T> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var first = -1;
        var count = 0;
        for (var i = 0; i < Items.Count; i++)
        {
            if (match(Items[i]))
            {
                first = count == 0 ? i : first;
                count++;
            }
        }

        if (count == 0)
        {
            return 0;
        }

        if (count == 1)
        {
            RemoveAt(first);
            return 1;
        }

        CheckReentrancy();
        if (Items is List<T> backing)
        {
            backing.RemoveAll(match);
        }
        else
        {
            var keep = Items.Where(x => !match(x)).ToList();
            Items.Clear();
            foreach (var item in keep)
            {
                Items.Add(item);
            }
        }

        RaiseReset();
        return count;
    }

    void IResettableList.ResetTo(IEnumerable items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ReplaceAll(items.OfType<T>());
    }

    private bool SameItems(IList<T> list)
    {
        if (list.Count != Items.Count)
        {
            return false;
        }

        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < list.Count; i++)
        {
            if (!comparer.Equals(list[i], Items[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
