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

        internal static bool TryStartElevatedInputHelper(
            string pipeName,
            int parentProcessId,
            out Process helperProcess,
            out string errorMessage)
        {
            helperProcess = null;
            errorMessage = null;
            if (IsAdministrator())
            {
                errorMessage = "앱 전체가 이미 관리자 권한으로 실행 중입니다.";
                return false;
            }

            string arguments = "--input-helper --pipe " + pipeName +
                " --parent " + parentProcessId.ToString(CultureInfo.InvariantCulture);

            try
            {
                helperProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath),
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (helperProcess == null)
                {
                    errorMessage = "관리자 입력 도우미 프로세스를 시작하지 못했습니다.";
                    return false;
                }
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
