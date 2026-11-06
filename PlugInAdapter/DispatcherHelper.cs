using System;
using System.Windows.Threading;

namespace RTSys.PlugIn
{
    internal static class DispatcherHelper
    {
        private static Dispatcher _dispatcher;

        public static void Capture()
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
        }

        public static T Invoke<T>(Func<T> func)
        {
            if (_dispatcher.CheckAccess())
                return func();
            return (T)_dispatcher.Invoke(new Func<T>(func));
        }

        public static void Invoke(Action action)
        {
            if (_dispatcher.CheckAccess())
                action();
            else
                _dispatcher.Invoke(action);
        }
    }
}
