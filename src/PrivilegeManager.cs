using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows.Forms;

namespace QuickWindowCapture
{
    internal static class PrivilegeManager
    {
        internal const string ElevatedArgument = "--elevated";
        private const string WaitForProcessArgument = "--wait-for-process";

        internal static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        internal static void WaitForPreviousInstance(string[] args)
        {
            for (int index = 0; index < args.Length - 1; index++)
            {
                if (!string.Equals(args[index], WaitForProcessArgument, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int processId;
                if (!int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out processId) ||
                    processId <= 0 || processId == Process.GetCurrentProcess().Id)
                {
                    return;
                }

                try
                {
                    using (Process previous = Process.GetProcessById(processId))
                    {
                        previous.WaitForExit(15000);
                    }
                }
                catch (ArgumentException)
                {
                }
                catch (InvalidOperationException)
                {
                }

                return;
            }
        }

        internal static bool TryRestartAsAdministrator(bool startHidden, out string errorMessage)
        {
            errorMessage = null;
            if (IsAdministrator())
            {
                return true;
            }

            string arguments = ElevatedArgument +
                " " + WaitForProcessArgument +
                " " + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
            if (startHidden)
            {
                arguments += " --startup";
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath),
                    UseShellExecute = true,
                    Verb = "runas"
                });
                return true;
            }
            catch (Win32Exception exception)
            {
                errorMessage = exception.NativeErrorCode == 1223
                    ? "관리자 권한 요청이 취소되었습니다."
                    : exception.Message;
                return false;
            }
            catch (Exception exception)
            {
                errorMessage = exception.Message;
                return false;
            }
        }
    }
}
