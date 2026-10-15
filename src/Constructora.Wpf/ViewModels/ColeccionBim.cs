using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Constructora.Wpf.ViewModels;

/// <summary>Actualiza la vista una sola vez por carga, incluso con modelos grandes.</summary>
public sealed class ColeccionBim<T> : ObservableCollection<T>
{
    public void Reemplazar(IEnumerable<T> values)
    {
        var items = values.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
