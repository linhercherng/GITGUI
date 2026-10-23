using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace GitGui.UI;

public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> values)
    {
        CheckReentrancy(); Items.Clear();
        foreach (var value in values) Items.Add(value);
        Changed();
    }
    public void AddRange(IEnumerable<T> values)
    {
        CheckReentrancy();
        foreach (var value in values) Items.Add(value);
        Changed();
    }
    private void Changed()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
