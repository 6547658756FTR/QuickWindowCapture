using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QuickWindowCapture
{
    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "QuickWindowCapture";

        internal static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (key == null)
                {
                    return false;
                }

                object value = key.GetValue(ValueName);
                return value != null && !string.IsNullOrWhiteSpace(value.ToString());
            }
        }

        internal static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("Windows 시작 프로그램 설정을 열지 못했습니다.");
                }

                if (enabled)
                {
                    string command = "\"" + Application.ExecutablePath + "\" --startup";
                    key.SetValue(ValueName, command, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }
}
