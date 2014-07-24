using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace VirtualCollection
{
    /// <summary>
    /// Implements a collection that loads its items by pages only when requested
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <remarks>The trick to ensuring that the silverlight datagrid doesn't attempt to enumerate all
    /// items from its DataSource in one shot is to implement both IList and ICollectionView.</remarks>
    public class VirtualCollection : IList<object>, IList, ICollectionView, INotifyPropertyChanged,
#if !SILVERLIGHT
                                        IItemProperties,
#endif
                                        IEnquireAboutItemVisibility //where T : class, new()
    {
        private const int IndividualItemNotificationLimit = 100;
        private const int MaxConcurrentPageRequests = 3;

        public event NotifyCollectionChangedEventHandler CollectionChanged;

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<QueryItemVisibilityEventArgs> QueryItemVisibility;
        public event EventHandler<ItemsRealizedEventArgs> ItemsRealized;
        public event CurrentChangingEventHandler CurrentChanging;
        public event EventHandler CurrentChanged;
        private readonly IVirtualCollectionSource _source;
        private readonly int _pageSize;
        private readonly IEqualityComparer<object> _equalityComparer;

        private uint _state; // used to ensure that data-requests are not stale

        public readonly SparseList<VirtualItem<object>> VirtualItems;
        private readonly HashSet<int> _fetchedPages = new HashSet<int>();
        private readonly HashSet<int> _requestedPages = new HashSet<int>();

        private readonly MostRecentUsedList<int> _mostRecentlyRequestedPages;
        private int _itemCount;
        private readonly TaskScheduler _synchronizationContextScheduler;
        private bool _isRefreshDeferred;
        private int _currentItem;

        private int _inProcessPageRequests;
        private Stack<PageRequest> _pendingPageRequests = new Stack<PageRequest>();

        private readonly SortDescriptionCollection _sortDescriptions = new SortDescriptionCollection();

        public VirtualCollection(IVirtualCollectionSource source, int pageSize, int cachedPages)
            : this(source, pageSize, cachedPages, EqualityComparer<object>.Default)
        { }

        public VirtualCollection(IVirtualCollectionSource source, int pageSize, int cachedPages, TaskScheduler taskScheduler)
            : this(source, pageSize, cachedPages, EqualityComparer<object>.Default, taskScheduler)
        { }

        public VirtualCollection(IVirtualCollectionSource source, int pageSize, int cachedPages,
            IEqualityComparer<object> equalityComparer)
            : this(source, pageSize, cachedPages, equalityComparer, null)
        { }

        public VirtualCollection(IVirtualCollectionSource source, int pageSize, int cachedPages,
                                 IEqualityComparer<object> equalityComparer, TaskScheduler taskScheduler)
        {
            if (pageSize < 1)
                throw new ArgumentException("pageSize must be bigger than 0");

            if (equalityComparer == null)
                throw new ArgumentNullException("equalityComparer");

            if (cachedPages < 8)
                cachedPages = 8;

            _source = source;
            _source.CollectionChanged += HandleSourceCollectionChanged;
            _source.CountChanged += HandleCountChanged;
            _pageSize = pageSize;
            _equalityComparer = equalityComparer;
            VirtualItems = CreateItemsCache(pageSize);
            _currentItem = -1;
            _synchronizationContextScheduler = taskScheduler ?? TaskScheduler.FromCurrentSynchronizationContext();
            _mostRecentlyRequestedPages = new MostRecentUsedList<int>(cachedPages);
            _mostRecentlyRequestedPages.ItemEvicted += HandlePageEvicted;

            (_sortDescriptions as INotifyCollectionChanged).CollectionChanged += HandleSortDescriptionsChanged;
        }



        public IVirtualCollectionSource Source
        {
            get { return _source; }
        }
        public CultureInfo Culture { get; set; }

        public IEnumerable SourceCollection
        {
            get { return this; }
        }

        public Predicate<object> Filter
        {
            get { throw new NotImplementedException(); }
            set { throw new NotImplementedException(); }
        }

        public bool CanFilter
        {
            get { return false; }
        }

        public SortDescriptionCollection SortDescriptions
        {
            get { return _sortDescriptions; }
        }

        public bool CanSort
        {
            get { return true; }
        }

        public bool CanGroup
        {
            get { return true; }
        }

        private ObservableCollection<GroupDescription> groupDescriptions = new ObservableCollection<GroupDescription>();
        public ObservableCollection<GroupDescription> GroupDescriptions
        {
            get { return groupDescriptions; }
        }

        private ReadOnlyObservableCollection<object> groups = new ReadOnlyObservableCollection<object>(new ObservableCollection<object>());
        public ReadOnlyObservableCollection<object> Groups
        {
            get { return groups; }
        }

        public void LoadGroups()
        {
            groups = _source.GetGroups(GroupDescriptions);
            OnPropertyChanged(new PropertyChangedEventArgs("Groups"));
        }

        public bool IsEmpty
        {
            get { return _itemCount == 0; }
        }

        public object CurrentItem
        {
            get { return 0 <= CurrentPosition && CurrentPosition < _itemCount ? VirtualItems[CurrentPosition].Item : null; }
        }

        public int CurrentPosition
        {
            get { return _currentItem; }
            private set
            {
                _currentItem = value;
                OnCurrentChanged(EventArgs.Empty);
            }
        }
        public bool IsCurrentAfterLast
        {
            get { return CurrentPosition >= _itemCount; }
        }

        public bool IsCurrentBeforeFirst
        {
            get { return CurrentPosition < 0; }
        }
        public int Count
        {
            get
            {
                if (_itemCount == 0)
                    UpdateCount();
                return _itemCount;
            }
        }

        object ICollection.SyncRoot
        {
            get { throw new NotImplementedException(); }
        }

        public bool IsSynchronized
        {
            get { return false; }
        }

        public bool IsReadOnly
        {
            get { return true; }
        }

        bool IList.IsFixedSize
        {
            get { return false; }
        }

        protected uint State
        {
            get { return _state; }
        }

        public void RealizeItemRequested(int index, bool byIndex)
        {
            var page = index / _pageSize;
            BeginGetPage(page);

            if (byIndex && ((Count / _pageSize) - 1 > page))
                BeginGetPage(page + 1, true);
            if (byIndex && (page > 0))
                BeginGetPage(page - 1, true);
        }

        private bool wasRefreshed;

        public void Refresh()
        {
            Refresh(RefreshMode.PermitStaleDataWhilstRefreshing);
        }

        public void Refresh(RefreshMode mode)
        {
            _fetchedPages.Clear();
            _itemCount = 0;
            firstCountChange = true;
            wasRefreshed = true;
            //_virtualItems.Clear();
            _requestedPages.Clear();

            if (!_isRefreshDeferred)
                _source.Refresh(mode);
        }

        private void HandlePageEvicted(object sender, ItemEvictedEventArgs<int> e)
        {
            _requestedPages.Remove(e.Item);
            _fetchedPages.Remove(e.Item);
            VirtualItems.RemoveRange(e.Item * _pageSize, _pageSize);
        }

        private SparseList<VirtualItem<object>> CreateItemsCache(int fetchPageSize)
        {
            // we don't want the sparse list to have pages that are too small,
            // because that will harm performance by fragmenting the list across memory,
            // but too big, and we'll be wasting lots of space
            const int TargetSparseListPageSize = 100;

            var pageSize = fetchPageSize;

            if (pageSize < TargetSparseListPageSize)
            {
                // make pageSize the smallest multiple of fetchPageSize that is bigger than TargetSparseListPageSize
                pageSize = (int)Math.Ceiling((double)TargetSparseListPageSize / pageSize) * pageSize;
            }

            return new SparseList<VirtualItem<object>>(pageSize);
        }

        private void HandleSortDescriptionsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            Refresh();
        }

        private void HandleCountChanged(object sender, EventArgs e)
        {
            Task.Factory.StartNew(UpdateCount, CancellationToken.None, TaskCreationOptions.None,
                                  _synchronizationContextScheduler);
        }

        private void HandleSourceCollectionChanged(object sender, VirtualCollectionSourceChangedEventArgs e)
        {
            if (e.ChangeType == ChangeType.Refresh)
            {
                Task.Factory.StartNew(UpdateData, CancellationToken.None,
                                      TaskCreationOptions.None, _synchronizationContextScheduler);
            }
            else if (e.ChangeType == ChangeType.Reset)
            {
                if (_fetchedPages.Count != 0 || _itemCount != 0)
                {
                    Task.Factory.StartNew(Reset, CancellationToken.None,
                        TaskCreationOptions.None, _synchronizationContextScheduler);
                }
            }
        }

        private void MarkExistingItemsAsStale()
        {
            foreach (var page in _fetchedPages)
            {
                var startIndex = page * _pageSize;
                var endIndex = (page + 1) * _pageSize;

                for (int i = startIndex; i < endIndex; i++)
                {
                    if (VirtualItems[i] != null)
                        VirtualItems[i].IsStale = true;
                }
            }
        }

        private void BeginGetPage(int page, bool previousNextRequest = false)
        {
            if (IsPageAlreadyRequested(page))
                return;

            _mostRecentlyRequestedPages.Add(page);
            _requestedPages.Add(page);

            _pendingPageRequests.Push(new PageRequest(page, State, previousNextRequest));
            
            ProcessPageRequests();
        }

        private void ProcessPageRequests()
        {
            while (_inProcessPageRequests < MaxConcurrentPageRequests && _pendingPageRequests.Count > 0)
            {
                var request = _pendingPageRequests.Pop();

                // if we encounter a requested posted for an early collection state,
                // we can ignore it, and all that came before it
                if (State != request.StateWhenRequested)
                {
                    _pendingPageRequests.Clear();
                    break;
                }

                // check that the page is still requested (the user might have scrolled, causing the 
                // page to be ejected from the cache
                if (!_requestedPages.Contains(request.Page))
                    break;

                _inProcessPageRequests++;

                var tsk = _source.GetPageAsync(request.Page * _pageSize, _pageSize, _sortDescriptions);
                tsk.ContinueWith(
                    t =>
                    {
                        if (!t.IsFaulted)
                            UpdatePage(request.Page, t.Result, request.StateWhenRequested, request.PreviousNextRequest);
                        else
                            MarkPageAsError(request.Page, request.StateWhenRequested);

                        // fire off any further requests
                        _inProcessPageRequests--;
                        ProcessPageRequests();
                    }, _synchronizationContextScheduler);

                tsk.Start();                
            }
        }

        private void MarkPageAsError(int page, uint stateWhenRequestInitiated)
        {
            if (stateWhenRequestInitiated != State)
                return;

            var stillRelevant = _requestedPages.Remove(page);
            if (!stillRelevant)
                return;

            var startIndex = page * _pageSize;

            for (int i = 0; i < _pageSize; i++)
            {
                var index = startIndex + i;
                var virtualItem = VirtualItems[index];
                if (virtualItem != null)
                    virtualItem.ErrorFetchingValue();
            }
        }

        private bool IsPageAlreadyRequested(int page)
        {
            return _fetchedPages.Contains(page) || _requestedPages.Contains(page);
        }

        private bool firstTimeIndexZero = true;
        private void UpdatePage(int page, IList results, uint stateWhenRequested, bool previousNextRequest)
        {
            if (stateWhenRequested != State)
            {
                // this request may contain out-of-date data, so ignore it
                return;
            }

            bool stillRelevant = _requestedPages.Remove(page);
            if (!stillRelevant)
                return;

            _fetchedPages.Add(page);

            var startIndex = page * _pageSize;

            // guard against rogue collection sources returning too many results
            var count = Math.Min(results.Count, _pageSize);

            for (int i = 0; i < count; i++)
            {
                var index = startIndex + i;
                var virtualItem = VirtualItems[index] ?? (VirtualItems[index] = new VirtualItem<object>(this, index));
                if (virtualItem.Item == null || results[i] == null || !_equalityComparer.Equals(virtualItem.Item, results[i]))
                    virtualItem.SupplyValue(results[i]);

                if (!previousNextRequest || virtualItem.IsAskedByIndex)
                {
#if SILVERLIGHT
                    if (firstTimeIndexZero && i == 0)
                    {
                        firstTimeIndexZero = false;
                        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                    }
                    else
                    {
                        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove,
                            VirtualItems[startIndex + i].Item, startIndex + i));
                        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add,
                            results[i], startIndex + i));
                    }
#else
                    OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove,
                            VirtualItems[startIndex + i].Item, startIndex + i));
                    OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add,
                            results[i], startIndex + i));
#endif
                }
            }

            if (count > 0)
            {
                OnItemsRealized(new ItemsRealizedEventArgs(startIndex, count));                
            }            
        }

        protected void UpdateData()
        {
            IncrementState();

            MarkExistingItemsAsStale();

            _fetchedPages.Clear();
            _requestedPages.Clear();

            UpdateCount();

            var queryItemVisibilityArgs = new QueryItemVisibilityEventArgs();
            OnQueryItemVisibility(queryItemVisibilityArgs);

            if (queryItemVisibilityArgs.FirstVisibleIndex.HasValue)
            {
                var firstVisiblePage = queryItemVisibilityArgs.FirstVisibleIndex.Value / _pageSize;
                var lastVisiblePage = queryItemVisibilityArgs.LastVisibleIndex.Value / _pageSize;

                int numberOfVisiblePages = lastVisiblePage - firstVisiblePage + 1;
                EnsurePageCacheSize(numberOfVisiblePages);

                for (int i = firstVisiblePage; i <= lastVisiblePage; i++)
                {
                    BeginGetPage(i);
                }
            }
            else
            {
                // in this case we have no way of knowing which items are currently visible,
                // so we signal a collection reset, and wait to see which pages are requested by the UI
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }

        private void IncrementState()
        {
            _state++;
        }

        private void EnsurePageCacheSize(int numberOfPages)
        {
            if (_mostRecentlyRequestedPages.Size < numberOfPages)
                _mostRecentlyRequestedPages.Size = numberOfPages;
        }

        private void Reset()
        {
            IncrementState();

            foreach (var page in _fetchedPages)
            {
                var startIndex = page*_pageSize;
                var endIndex = (page + 1)*_pageSize;

                for (int i = startIndex; i < endIndex; i++)
                {
                    if (VirtualItems[i] != null)
                        VirtualItems[i].ClearValue();
                }
            }

            _fetchedPages.Clear();
            _requestedPages.Clear();

            _currentItem = -1;
            UpdateCount(0);

            //OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

            UpdateCount();

            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        private void UpdateCount()
        {
            if (_source.Count.HasValue)
            {
                UpdateCount(_source.Count.Value);
            }
            else
            {
                // if the Count is null, that indicates that
                // the VirtualCollectionSource needs us to fetch a page before it will know how many elements there are
                BeginGetPage(0);
            }
        }

        private bool firstCountChange = true;

        private void UpdateCount(int count)
        {
            if (_itemCount == count & !wasRefreshed)
                return;

            wasRefreshed = false;

            var wasCurrentBeyondLast = IsCurrentAfterLast;

            var originalItemCount = _itemCount;
            var delta = count - originalItemCount;
            _itemCount = count;

            if (IsCurrentAfterLast && !wasCurrentBeyondLast)
                UpdateCurrentPosition(_itemCount - 1, allowCancel: false);

            OnPropertyChanged(new PropertyChangedEventArgs("Count"));

            if ((delta > 0 && firstCountChange) || Math.Abs(delta) > IndividualItemNotificationLimit || _itemCount == 0)
            {
                firstCountChange = false;
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
            else if (delta > 0)
            {
                for (int i = 0; i < delta; i++)
                {
                    OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, 
                                                                             VirtualItems[originalItemCount + i],
                                                                             originalItemCount + i));
                }
            }
            else if (delta < 0)
            {
                for (int i = 1; i <= Math.Abs(delta); i++)
                {
                    var itm = VirtualItems[originalItemCount - i];
                    if (itm != null)
                    {
                        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove,
                            itm,
                            originalItemCount - i));
                    }
                }
            }
        }

        public int IndexOf(object item)
        {
            return 0;
            //todo
            //return _virtualItems.
            //return item.Index;
        }

        public bool Contains(object item)
        {
            return true;
            //todo
            //return item is VirtualItem<T> && Contains(item as VirtualItem<T>);
        }

        object IList.this[int index]
        {
            get { return getbyIndex(index, true); }
            set { throw new NotImplementedException(); }
        }

        public object this[int index]
        {
            get
            {
                return getbyIndex(index, false);
            }
            set { throw new NotImplementedException(); }
        }

        private object getbyIndex(int index, bool byIlist)
        {
            if (index >= Count)
            {
                throw new ArgumentOutOfRangeException("index");
            }

            if (byIlist)
                RealizeItemRequested(index, true);

            if (!byIlist)
                return null;

            var itm = VirtualItems[index] ?? (VirtualItems[index] = new VirtualItem<object>(this, index));

            itm.IsAskedByIndex = byIlist;

            return itm.Item;
        }

        public IDisposable DeferRefresh()
        {
            _isRefreshDeferred = true;

            return new Disposer(() =>
            {
                _isRefreshDeferred = false;
                Refresh();
            });
        }

        public bool MoveCurrentToFirst()
        {
            return UpdateCurrentPosition(0);
        }

        public bool MoveCurrentToLast()
        {
            return UpdateCurrentPosition(_itemCount - 1);
        }

        public bool MoveCurrentToNext()
        {
            return UpdateCurrentPosition(CurrentPosition + 1);
        }

        public bool MoveCurrentToPrevious()
        {
            return UpdateCurrentPosition(CurrentPosition - 1);
        }

        public bool MoveCurrentTo(object item)
        {
            return MoveCurrentToPosition(((IList)this).IndexOf(item));
        }

        public bool MoveCurrentToPosition(int position)
        {
            return UpdateCurrentPosition(position);
        }

        private bool UpdateCurrentPosition(int newCurrentPosition, bool allowCancel = true)
        {
            var changingEventArgs = new CurrentChangingEventArgs(allowCancel);

            OnCurrentChanging(changingEventArgs);

            if (!changingEventArgs.Cancel)
            {
                CurrentPosition = newCurrentPosition;
            }

            return !IsCurrentBeforeFirst && !IsCurrentAfterLast;
        }

        protected void OnCurrentChanging(CurrentChangingEventArgs e)
        {
            var handler = CurrentChanging;
            if (handler != null) handler(this, e);
        }


        protected void OnCurrentChanged(EventArgs e)
        {
            var handler = CurrentChanged;
            if (handler != null) handler(this, e);
        }

        protected void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            var handler = CollectionChanged;
            if (handler != null) handler(this, e);
        }

        protected void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, e);
        }

        protected void OnItemsRealized(ItemsRealizedEventArgs e)
        {
            var handler = ItemsRealized;
            if (handler != null) handler(this, e);
        }

        protected void OnQueryItemVisibility(QueryItemVisibilityEventArgs e)
        {
            var handler = QueryItemVisibility;
            if (handler != null) handler(this, e);
        }

        public IEnumerator<object> GetEnumerator()
        {
            for (var i = 0; i < _itemCount; i++)
            {
                yield return getbyIndex(i, false);
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void CopyTo(object[] array, int arrayIndex)
        {
            throw new NotImplementedException();
        }

        void ICollection.CopyTo(Array array, int index)
        {
            throw new NotImplementedException();
        }

        public void Add(object item)
        {
            throw new NotImplementedException();
        }

        int IList.Add(object value)
        {
            throw new NotImplementedException();
        }

        bool IList.Contains(object value)
        {
            return value is VirtualItem<object> && Contains(value as VirtualItem<object>);
        }

        public void Clear()
        {
            throw new NotImplementedException();
        }

        int IList.IndexOf(object value)
        {
            var itm = VirtualItems.FirstOrDefault(x => x != null && x.Item == value);

            if (itm != null)
                return itm.Index;
            return -1;
        }

        void IList.Insert(int index, object value)
        {
            throw new NotImplementedException();
        }

        void IList.Remove(object value)
        {
            throw new NotImplementedException();
        }

        public bool Remove(object item)
        {
            throw new NotImplementedException();
        }

        public void Insert(int index, object item)
        {
            throw new NotImplementedException();
        }

        public void RemoveAt(int index)
        {
            throw new NotImplementedException();
        }

        private struct PageRequest
        {
            public readonly int Page;
            public readonly uint StateWhenRequested;

            public readonly bool PreviousNextRequest;

            public PageRequest(int page, uint state, bool previousNextRequest)
            {
                Page = page;
                StateWhenRequested = state;
                PreviousNextRequest = previousNextRequest;
            }
        }

#if !SILVERLIGHT
        private ReadOnlyCollection<ItemPropertyInfo> _itemProperties;

        public ReadOnlyCollection<ItemPropertyInfo> ItemProperties
        {
            get
            {
                if (_itemProperties == null)
                {
                    List<ItemPropertyInfo> retVal = new List<ItemPropertyInfo>();
                    foreach (var propertyInfo in _source.CollectionType.GetProperties())
                    {
                        retVal.Add(new ItemPropertyInfo(propertyInfo.Name, propertyInfo.PropertyType, propertyInfo));
                    }

                    _itemProperties = new ReadOnlyCollection<ItemPropertyInfo>(retVal);
                }
                return _itemProperties;
            }
        }
#endif
    }
}
