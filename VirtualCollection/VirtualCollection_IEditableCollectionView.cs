using System;
using System.ComponentModel;

namespace VirtualCollection
{
    public partial class VirtualCollection
#if !NETFX_CORE
                                            : IEditableCollectionView
#endif
    {
        public object AddNew()
        {
            CurrentAddItem = ItemAdd?.Invoke();
            IsAddingNew = true;
            return CurrentAddItem;
        }

        public void CommitNew()
        {
            ItemAddFinished?.Invoke(CurrentAddItem);
            IsAddingNew = false;
            CurrentAddItem = null;
        }

        public void CancelNew()
        {
            ItemAddCanceled?.Invoke(CurrentAddItem);
            IsAddingNew = false;
            CurrentAddItem = null;
        }

        void IEditableCollectionView.Remove(object item)
        {
            ItemRemoved?.Invoke(CurrentEditItem);
        }

        public void EditItem(object item)
        {
            CurrentEditItem = item;
            ItemEditStarted?.Invoke(item);
            IsEditingItem = true;
        }

        public void CommitEdit()
        {
            ItemEditFinished?.Invoke(CurrentEditItem);
            CurrentEditItem = null;
            IsEditingItem = false;
        }

        public void CancelEdit()
        {
            ItemEditCanceled?.Invoke(CurrentEditItem);
            CurrentEditItem = null;
            IsEditingItem = false;
        }

        public NewItemPlaceholderPosition NewItemPlaceholderPosition { get; set; }

        public bool CanAddNew { get; set; }

        public bool IsAddingNew { get; private set; }

        public object CurrentAddItem { get; private set; }

        public bool CanRemove { get; set; }

        public bool CanCancelEdit { get; set; }

        public bool IsEditingItem { get; private set; }

        public object CurrentEditItem { get; private set; }

        public event Action<object> ItemEditStarted;
        public event Action<object> ItemEditFinished;
        public event Action<object> ItemEditCanceled;
        public event Action<object> ItemRemoved;
        public event Func<object> ItemAdd;
        public event Action<object> ItemAddFinished;
        public event Action<object> ItemAddCanceled;
    }
}
