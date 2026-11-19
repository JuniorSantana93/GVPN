using System;
using System.Threading;
using System.Windows;

namespace GVPN
{
    public partial class App : System.Windows.Application
    {
        private const string InstanceMutexName = "Local\\GVPN.SingleInstance";
        private const string ActivationEventName = "Local\\GVPN.ActivateExisting";
        private Mutex? _instanceMutex;
        private EventWaitHandle? _activationEvent;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _instanceMutex = new Mutex(true, InstanceMutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                SignalExistingInstance();
                Shutdown();
                return;
            }

            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            var activationThread = new Thread(WaitForActivation)
            {
                IsBackground = true,
                Name = "GVPN activation listener"
            };
            activationThread.Start();

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private static void SignalExistingInstance()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                    activationEvent.Set();
                    return;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException)
                {
                    return;
                }
            }
        }

        private void WaitForActivation()
        {
            while (true)
            {
                try { _activationEvent?.WaitOne(); }
                catch (ObjectDisposedException) { return; }

                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.BringToForeground());
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { _activationEvent?.Set(); } catch (ObjectDisposedException) { }
            _activationEvent?.Dispose();

            if (_instanceMutex is not null)
            {
                try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
                _instanceMutex.Dispose();
                _instanceMutex = null;
            }

            base.OnExit(e);
        }
    }
}
