using System;
using System.Threading;
using System.Windows.Forms;

namespace QuickWindowCapture
{
    internal static class Program
    {
        private const string MutexName = @"Local\QuickWindowCapture.48D27AE2";
        private const string ShowEventName = @"Local\QuickWindowCapture.ShowSettings.48D27AE2";

        [STAThread]
        private static void Main(string[] args)
        {
            // This must be the first native-policy operation. The application has
            // no private DLL dependencies, so every P/Invoke is restricted to the
            // trusted Windows system directory before UI or capture components load.
            NativeMethods.RequireSystemDllSearchOnly();
            NativeMethods.EnableBestDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (ElevatedInputHelper.IsHelperInvocation(args))
            {
                ElevatedInputHelper.Run(args);
                return;
            }

            bool eventCreated;
            using (EventWaitHandle showEvent = new EventWaitHandle(
                false,
                EventResetMode.AutoReset,
                ShowEventName,
                out eventCreated))
            {
                bool isFirstInstance;
                using (Mutex mutex = new Mutex(true, MutexName, out isFirstInstance))
                {
                    if (!isFirstInstance)
                    {
                        showEvent.Set();
                        return;
                    }

                    bool startedByWindows = HasArgument(args, "--startup");
                    using (TrayApplicationContext context = new TrayApplicationContext(!startedByWindows, showEvent))
                    {
                        Application.Run(context);
                    }

                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                    }
                }
            }
        }

        private static bool HasArgument(string[] args, string expected)
        {
            foreach (string argument in args)
            {
                if (string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
