﻿using System;
using System.Net;

namespace VirtualCollection
{
    public class Disposer : IDisposable
    {
        private readonly Action _action;

        public Disposer(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            _action();
        }
    }
}
