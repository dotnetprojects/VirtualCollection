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
            throw new NotImplementedException();
        }

        public void CommitNew()
        {
        }

        public void CancelNew()
        {
            throw new NotImplementedException();
        }

        void IEditableCollectionView.Remove(object item)
        {
            throw new NotImplementedException();
        }

        public void EditItem(object item)
        {
            CurrentEditItem = item;
            IsEditingItem = true;
        }

        public void CommitEdit()
        {
            CurrentEditItem = null;
            IsEditingItem = false;
        }

        public void CancelEdit()
        {
            CurrentEditItem = null;
            IsEditingItem = false;
        }

        public NewItemPlaceholderPosition NewItemPlaceholderPosition { get; set; }

        public bool CanAddNew { get; private set; }

        public bool IsAddingNew { get; private set; }

        public object CurrentAddItem { get; private set; }

        public bool CanRemove { get; private set; }

        public bool CanCancelEdit { get; private set; }

        public bool IsEditingItem { get; private set; }

        public object CurrentEditItem { get; private set; }
    }
}
