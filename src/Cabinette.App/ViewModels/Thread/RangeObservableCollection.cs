using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>ObservableCollection with a single-notification bulk replace (one Reset instead of thousands of Add events).</summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
