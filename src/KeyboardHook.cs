using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuickWindowCapture
{
    internal sealed class KeyboardHook : IDisposable
    {
        private const int VkControl = 0x11;
        private const int VkMenu = 0x12;
        private const int VkShift = 0x10;
        private const int VkLeftWindows = 0x5B;
        private const int VkRightWindows = 0x5C;

        private readonly NativeMethods.LowLevelKeyboardProc callback;
        private readonly CaptureKeyLatch captureKeyLatch;
        private IntPtr hookHandle;
        private AppSettings settings;
        private int consumedKey = -1;

        internal event Action<IntPtr> CaptureRequested;

        internal KeyboardHook(AppSettings initialSettings)
        {
            settings = initialSettings.Copy();
            callback = HookCallback;
            captureKeyLatch = new CaptureKeyLatch();
        }

        internal void Start()
        {
            if (hookHandle != IntPtr.Zero)
            {
                return;
            }

            hookHandle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                callback,
                NativeMethods.GetModuleHandle(null),
                0);

            if (hookHandle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "전역 단축키를 시작하지 못했습니다.");
            }
        }

        internal void UpdateSettings(AppSettings updatedSettings)
        {
            settings = updatedSettings.Copy();
            consumedKey = -1;
            captureKeyLatch.Reset();
        }

        private IntPtr HookCallback(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0)
            {
                int messageValue = message.ToInt32();
                bool isKeyDown = messageValue == NativeMethods.WM_KEYDOWN || messageValue == NativeMethods.WM_SYSKEYDOWN;
                bool isKeyUp = messageValue == NativeMethods.WM_KEYUP || messageValue == NativeMethods.WM_SYSKEYUP;
                int virtualKey = Marshal.ReadInt32(data);

                if (isKeyDown && virtualKey == (int)settings.CaptureKey && CurrentModifiers() == settings.Modifiers)
                {
                    if (captureKeyLatch.TryPress())
                    {
                        consumedKey = virtualKey;
                        IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
                        Action<IntPtr> handler = CaptureRequested;
                        if (handler != null && foregroundWindow != IntPtr.Zero)
                        {
                            handler(foregroundWindow);
                        }
                    }

                    return new IntPtr(1);
                }

                if (isKeyUp && virtualKey == (int)settings.CaptureKey)
                {
                    captureKeyLatch.Release();
                    if (virtualKey == consumedKey)
                    {
                        consumedKey = -1;
                        return new IntPtr(1);
                    }
                }
            }

            return NativeMethods.CallNextHookEx(hookHandle, code, message, data);
        }

        private static HotkeyModifiers CurrentModifiers()
        {
            HotkeyModifiers result = HotkeyModifiers.None;
            if (IsDown(VkControl)) result |= HotkeyModifiers.Control;
            if (IsDown(VkMenu)) result |= HotkeyModifiers.Alt;
            if (IsDown(VkShift)) result |= HotkeyModifiers.Shift;
            if (IsDown(VkLeftWindows) || IsDown(VkRightWindows)) result |= HotkeyModifiers.Windows;
            return result;
        }

        private static bool IsDown(int virtualKey)
        {
            return (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        }

        public void Dispose()
        {
            if (hookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(hookHandle);
                hookHandle = IntPtr.Zero;
            }
        }
    }
}
