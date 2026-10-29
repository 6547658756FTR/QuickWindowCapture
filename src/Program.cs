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
            PrivilegeManager.WaitForPreviousInstance(args);
            NativeMethods.EnableBestDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

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
                    bool elevationRequested = AppSettings.Load().RunAsAdministrator &&
                        !PrivilegeManager.IsAdministrator() &&
                        !HasArgument(args, PrivilegeManager.ElevatedArgument);
                    if (elevationRequested)
                    {
                        string elevationError;
                        if (PrivilegeManager.TryRestartAsAdministrator(startedByWindows, out elevationError))
                        {
                            return;
                        }

                        if (!startedByWindows && !string.IsNullOrEmpty(elevationError))
                        {
                            MessageBox.Show(
                                "관리자 호환 모드로 시작하지 못했습니다. 일반 권한으로 계속 실행합니다.\r\n\r\n" +
                                elevationError,
                                "빠른 창 캡처",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information);
                        }
                    }

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
