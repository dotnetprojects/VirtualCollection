using System;

namespace VirtualCollection
{
    public interface INotifyBusyness
    {
        event EventHandler<EventArgs> IsBusyChanged;
        bool IsBusy { get; }
    }
}
