using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuickWindowCapture
{
    // A second, non-blocking input path for games that do not reliably deliver
    // key events through a low-level keyboard hook. Raw Input never consumes the
    // key, so the foreground application continues to receive it normally.
    internal sealed class RawInputMonitor : NativeWindow, IDisposable
    {
        private const int WmInput = 0x00FF;
        private const uint RidInput = 0x10000003;
        private const uint RimTypeKeyboard = 1;
        private const uint RidevInputSink = 0x00000100;
        private const uint RidevRemove = 0x00000001;
        private static readonly IntPtr MessageOnlyWindow = new IntPtr(-3);

        private readonly HashSet<int> pressedKeys = new HashSet<int>();
        private readonly CaptureKeyLatch captureKeyLatch = new CaptureKeyLatch();
        private AppSettings settings;
        private bool disposed;

        internal event Action<IntPtr> CaptureRequested;

        internal RawInputMonitor(AppSettings initialSettings)
        {
            settings = initialSettings.Copy();

            CreateParams parameters = new CreateParams();
            parameters.Caption = "QuickWindowCapture.RawInput";
            parameters.Parent = MessageOnlyWindow;
            CreateHandle(parameters);

            RawInputDevice[] devices =
            {
                new RawInputDevice
                {
                    UsagePage = 0x01,
                    Usage = 0x06,
                    Flags = RidevInputSink,
                    TargetWindow = Handle
                }
            };

            if (!RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf(typeof(RawInputDevice))))
            {
                int error = Marshal.GetLastWin32Error();
                DestroyHandle();
                throw new Win32Exception(error, "Raw Input 키보드 감지를 시작하지 못했습니다.");
            }
        }

        internal void UpdateSettings(AppSettings updatedSettings)
        {
            settings = updatedSettings.Copy();
            pressedKeys.Clear();
            captureKeyLatch.Reset();
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmInput)
            {
                ProcessRawInput(message.LParam);
            }

            base.WndProc(ref message);
        }

        private void ProcessRawInput(IntPtr rawInputHandle)
        {
            uint size = 0;
            uint headerSize = (uint)Marshal.SizeOf(typeof(RawInputHeader));
            uint firstResult = GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize);
            if (firstResult == uint.MaxValue || size < headerSize)
            {
                return;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                uint copiedSize = size;
                uint result = GetRawInputData(rawInputHandle, RidInput, buffer, ref copiedSize, headerSize);
                if (result == uint.MaxValue || copiedSize < headerSize)
                {
                    return;
                }

                RawInputHeader header = (RawInputHeader)Marshal.PtrToStructure(buffer, typeof(RawInputHeader));
                if (header.Type != RimTypeKeyboard)
                {
                    return;
                }

                IntPtr keyboardPointer = IntPtr.Add(buffer, (int)headerSize);
                RawKeyboard keyboard = (RawKeyboard)Marshal.PtrToStructure(keyboardPointer, typeof(RawKeyboard));
                bool isKeyDown = keyboard.Message == NativeMethods.WM_KEYDOWN || keyboard.Message == NativeMethods.WM_SYSKEYDOWN;
                bool isKeyUp = keyboard.Message == NativeMethods.WM_KEYUP || keyboard.Message == NativeMethods.WM_SYSKEYUP;
                if (!isKeyDown && !isKeyUp)
                {
                    return;
                }

                ProcessKeyEvent(keyboard.VirtualKey, isKeyDown);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        // Kept separate from WM_INPUT parsing so modifier and repeat behavior can
        // be verified without needing a physical keyboard event.
        internal void ProcessKeyEvent(int virtualKey, bool isKeyDown)
        {
            if (isKeyDown)
            {
                pressedKeys.Add(virtualKey);
            }
            else
            {
                pressedKeys.Remove(virtualKey);
            }

            if (virtualKey != (int)settings.CaptureKey)
            {
                return;
            }

            if (!isKeyDown)
            {
                captureKeyLatch.Release();
                return;
            }

            if (CurrentModifiers() != settings.Modifiers || !captureKeyLatch.TryPress())
            {
                return;
            }

            IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
            Action<IntPtr> handler = CaptureRequested;
            if (handler != null && foregroundWindow != IntPtr.Zero)
            {
                handler(foregroundWindow);
            }
        }

        private HotkeyModifiers CurrentModifiers()
        {
            HotkeyModifiers result = HotkeyModifiers.None;
            if (IsAnyPressed((int)Keys.ControlKey, (int)Keys.LControlKey, (int)Keys.RControlKey))
                result |= HotkeyModifiers.Control;
            if (IsAnyPressed((int)Keys.Menu, (int)Keys.LMenu, (int)Keys.RMenu))
                result |= HotkeyModifiers.Alt;
            if (IsAnyPressed((int)Keys.ShiftKey, (int)Keys.LShiftKey, (int)Keys.RShiftKey))
                result |= HotkeyModifiers.Shift;
            if (IsAnyPressed((int)Keys.LWin, (int)Keys.RWin))
                result |= HotkeyModifiers.Windows;
            return result;
        }

        private bool IsAnyPressed(params int[] keys)
        {
            foreach (int key in keys)
            {
                if (pressedKeys.Contains(key))
                {
                    return true;
                }
            }
            return false;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (Handle != IntPtr.Zero)
            {
                RawInputDevice[] devices =
                {
                    new RawInputDevice
                    {
                        UsagePage = 0x01,
                        Usage = 0x06,
                        Flags = RidevRemove,
                        TargetWindow = IntPtr.Zero
                    }
                };
                RegisterRawInputDevices(
                    devices,
                    (uint)devices.Length,
                    (uint)Marshal.SizeOf(typeof(RawInputDevice)));
                DestroyHandle();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputDevice
        {
            internal ushort UsagePage;
            internal ushort Usage;
            internal uint Flags;
            internal IntPtr TargetWindow;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawInputHeader
        {
            internal uint Type;
            internal uint Size;
            internal IntPtr Device;
            internal IntPtr Parameter;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawKeyboard
        {
            internal ushort MakeCode;
            internal ushort Flags;
            internal ushort Reserved;
            internal ushort VirtualKey;
            internal uint Message;
            internal uint ExtraInformation;
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterRawInputDevices(
            [In] RawInputDevice[] devices,
            uint deviceCount,
            uint deviceSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(
            IntPtr rawInput,
            uint command,
            IntPtr data,
            ref uint size,
            uint headerSize);
    }
}
