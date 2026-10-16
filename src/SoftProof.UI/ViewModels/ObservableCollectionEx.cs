using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SoftProof.UI.ViewModels;

/// <summary>
/// ObservableCollection with AddRange/RemoveRange and bulk change notifications
/// </summary>
public class ObservableCollectionEx<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;
    
    public void AddRange(IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        
        _suppressNotifications = true;
        try
        {
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppressNotifications = false;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
    
    public void RemoveRange(IEnumerable<T> items)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        
        _suppressNotifications = true;
        try
        {
            foreach (var item in items.ToList())
            {
                Items.Remove(item);
            }
        }
        finally
        {
            _suppressNotifications = false;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
    
    public void ReplaceAll(IEnumerable<T> items)
    {
        _suppressNotifications = true;
        try
        {
            Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }
        }
        finally
        {
            _suppressNotifications = false;
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
    
    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnCollectionChanged(e);
    }
    
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnPropertyChanged(e);
    }
}