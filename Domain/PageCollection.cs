using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace XTPdfMergeApp.Domain;

internal sealed class PageCollection : ObservableCollection<PagePlacement>
{
    public void AddRange(IEnumerable<PagePlacement> pages)
    {
        CheckReentrancy();
        var batch = pages.ToList();
        if (batch.Count == 0) return;
        foreach (var page in batch) Items.Add(page);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
