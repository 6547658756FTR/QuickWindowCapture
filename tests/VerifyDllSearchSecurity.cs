using System;
using System.IO;
using System.Runtime.InteropServices;
using QuickWindowCapture;

internal static class VerifyDllSearchSecurity
{
    private static int Main()
    {
        NativeMethods.RequireSystemDllSearchOnly();

        bool enabled;
        DwmIsCompositionEnabled(out enabled);

        IntPtr module = GetModuleHandle("dwmapi.dll");
        Require(module != IntPtr.Zero, "dwmapi.dll이 로드되지 않았습니다.");

        char[] pathBuffer = new char[32768];
        uint pathLength = GetModuleFileName(module, pathBuffer, (uint)pathBuffer.Length);
        Require(pathLength > 0 && pathLength < pathBuffer.Length, "DLL 경로를 확인하지 못했습니다.");

        string actualPath = Path.GetFullPath(new string(pathBuffer, 0, (int)pathLength));
        string expectedPath = Path.GetFullPath(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "dwmapi.dll"));
        Require(
            string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase),
            "시스템 폴더가 아닌 위치에서 DLL이 로드되었습니다: " + actualPath);

        Console.WriteLine("시스템 DLL 검색 경로 제한 테스트 통과: " + actualPath);
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(out bool enabled);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileName(IntPtr module, char[] fileName, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);
}
