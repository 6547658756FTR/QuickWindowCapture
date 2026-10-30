using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace QuickWindowCapture
{
    internal static class DiagnosticLog
    {
        private const long MaximumLogBytes = 512 * 1024;
        private readonly static object SyncRoot = new object();

        private static string LogFile
        {
            get { return Path.Combine(AppSettings.SettingsFolder, "diagnostic.log"); }
        }

        internal static void Write(string message)
        {
            try
            {
                lock (SyncRoot)
                {
                    Directory.CreateDirectory(AppSettings.SettingsFolder);
                    FileInfo log = new FileInfo(LogFile);
                    if (log.Exists && log.Length > MaximumLogBytes)
                    {
                        File.WriteAllText(
                            LogFile,
                            "이전 진단 로그가 용량 제한으로 초기화되었습니다.\r\n",
                            new UTF8Encoding(false));
                    }

                    string line = DateTime.Now.ToString(
                        "yyyy-MM-dd HH:mm:ss.fff",
                        CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine;
                    File.AppendAllText(LogFile, line, new UTF8Encoding(false));
                }
            }
            catch
            {
                // Diagnostics must never interfere with capture or input handling.
            }
        }
    }
}
