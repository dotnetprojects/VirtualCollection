using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;

namespace VirtualCollection
{
    public interface IVirtualCollectionSource
    {
        Type CollectionType { get; }

        event EventHandler<VirtualCollectionSourceChangedEventArgs> CollectionChanged;
        event EventHandler<EventArgs> CountChanged;

        int? Count { get; }
        void Refresh(RefreshMode mode);
        Task<IList> GetPageAsync(int start, int pageSize, IList<SortDescription> sortDescriptions);

        ReadOnlyObservableCollection<object> GetGroups(ObservableCollection<GroupDescription> groupDescriptions);
    }
    public interface IVirtualCollectionSource<T> : IVirtualCollectionSource
    {
        Task<IList<T>> GetPageAsyncT(int start, int pageSize, IList<SortDescription> sortDescriptions);
    }

    public class VirtualCollectionSourceChangedEventArgs : EventArgs
    {
        public ChangeType ChangeType { get; private set; }

        public VirtualCollectionSourceChangedEventArgs(ChangeType changeType)
        {
            ChangeType = changeType;
        }
    }

    public enum ChangeType
    {
        /// <summary>
        /// Current data is invalid and should be cleared
        /// </summary>
        Reset,
        /// <summary>
        /// Current data may still be valid, and can be shown whilst refreshing
        /// </summary>
        Refresh,
    }
}
