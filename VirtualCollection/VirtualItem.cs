using System;
using System.ComponentModel;

namespace VirtualCollection
{
    public class VirtualItem<T> : INotifyPropertyChanged where T : class, new()
    {
        private readonly VirtualCollection _parent;
        private readonly int _index;
        private T _item;
        private bool _isStale;
        private bool dataFetchError;

        internal bool IsAskedByIndex { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;

        public VirtualItem(VirtualCollection parent, int index)
        {
            _parent = parent;
            _index = index;
            Item = new T();
        }

        public T Item
        {
            get
            {
                if (!IsRealized && !DataFetchError && IsAskedByIndex)
                {
                    _parent.RealizeItemRequested(Index, false);
                }
                return _item;
            }
            private set
            {
                _item = value;
                OnPropertyChanged(new PropertyChangedEventArgs("Item"));
                OnPropertyChanged(new PropertyChangedEventArgs("IsRealized"));
                IsStale = false;
            }
        }

        public bool IsStale
        {
            get { return _isStale; }
            set
            {
                _isStale = value;
                OnPropertyChanged(new PropertyChangedEventArgs("IsStale"));
            }
        }

        public void SupplyValue(T value)
        {
            DataFetchError = false;
            Item = value;
        }

        public void ClearValue()
        {
            DataFetchError = false;
            Item = new T();
        }

        public void ErrorFetchingValue()
        {
            Item = new T();
            DataFetchError = true;
        }

        public bool DataFetchError
        {
            get { return dataFetchError; }
            private set
            {
                dataFetchError = value;
                OnPropertyChanged(new PropertyChangedEventArgs("DataFetchError"));
            }
        }

#if SILVERLIGHT
        public bool IsRealized { get { return _item.GetType() != typeof(object); } }
#else
        public bool IsRealized { get { return _item != null; } }
#endif

        public int Index
        {
            get { return _index; }
        }

        public VirtualCollection Parent
        {
            get { return _parent; }
        }

        protected void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null) handler(this, e);
        }
    }
}
