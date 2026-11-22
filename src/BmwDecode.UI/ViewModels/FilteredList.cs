using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace BmwDecode.App.ViewModels;

/// <summary>
/// A filtered (and optionally sorted) live view of an <see cref="ObservableCollection{T}"/> that any UI toolkit can
/// bind to — the platform-neutral replacement for WPF's ICollectionView. Call <see cref="Refresh"/> when the filter
/// or sort inputs change; changes to the source are followed automatically. Must be used on the UI thread.
/// </summary>
public sealed class FilteredList<T> : ObservableCollection<T>
{
    private readonly ObservableCollection<T> _source;
    private readonly Func<T, bool> _filter;
    private readonly IComparer<T>? _sort;

    public FilteredList(ObservableCollection<T> source, Func<T, bool> filter, IComparer<T>? sort = null)
    {
        _source = source;
        _filter = filter;
        _sort = sort;
        _source.CollectionChanged += OnSourceChanged;
        Refresh();
    }

    public ObservableCollection<T> Source => _source;

    public void Refresh()
    {
        IEnumerable<T> items = _source.Where(_filter);
        if (_sort is not null)
        {
            items = items.OrderBy(x => x, _sort);
        }

        var list = items.ToList();
        Items.Clear();
        foreach (var item in list)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Appends without sorting (the session log) stay cheap; everything else rebuilds.
        if (e.Action == NotifyCollectionChangedAction.Add && _sort is null && e.NewItems is { } added
            && e.NewStartingIndex == _source.Count - added.Count)
        {
            foreach (T item in added)
            {
                if (_filter(item))
                {
                    Add(item);
                }
            }

            return;
        }

        Refresh();
    }
}
