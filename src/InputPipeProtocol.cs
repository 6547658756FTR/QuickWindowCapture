using System;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace QuickWindowCapture
{
    internal static class InputPipeProtocol
    {
        internal const string HelloMessage = "HELLO|2.0.0";
        internal const string ExitMessage = "EXIT";

        internal static string CreateConfiguration(AppSettings settings)
        {
            return "CONFIG|" +
                ((int)settings.CaptureKey).ToString(CultureInfo.InvariantCulture) + "|" +
                ((int)settings.Modifiers).ToString(CultureInfo.InvariantCulture);
        }

        internal static bool TryParseConfiguration(string message, out AppSettings settings)
        {
            settings = null;
            string[] parts = Split(message, 3);
            if (parts == null || !string.Equals(parts[0], "CONFIG", StringComparison.Ordinal))
            {
                return false;
            }

            int keyValue;
            int modifierValue;
            int allowedModifiers = (int)(HotkeyModifiers.Control |
                HotkeyModifiers.Alt |
                HotkeyModifiers.Shift |
                HotkeyModifiers.Windows);
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out keyValue) ||
                keyValue <= 0 || keyValue > 255 ||
                !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out modifierValue) ||
                modifierValue < 0 || (modifierValue & ~allowedModifiers) != 0)
            {
                return false;
            }

            settings = AppSettings.CreateDefault();
            settings.CaptureKey = (Keys)keyValue;
            settings.Modifiers = (HotkeyModifiers)modifierValue;
            return true;
        }

        internal static string CreateCapture(IntPtr windowHandle)
        {
            return "CAPTURE|" + windowHandle.ToInt64().ToString(CultureInfo.InvariantCulture);
        }

        internal static bool TryParseCapture(string message, out IntPtr windowHandle)
        {
            windowHandle = IntPtr.Zero;
            string[] parts = Split(message, 2);
            long value;
            if (parts == null || !string.Equals(parts[0], "CAPTURE", StringComparison.Ordinal) ||
                !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ||
                value == 0)
            {
                return false;
            }

            windowHandle = new IntPtr(value);
            return true;
        }

        internal static string CreateError(string message)
        {
            string safeMessage = message ?? string.Empty;
            return "ERROR|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(safeMessage));
        }

        internal static bool TryParseError(string message, out string errorMessage)
        {
            errorMessage = null;
            string[] parts = Split(message, 2);
            if (parts == null || !string.Equals(parts[0], "ERROR", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                byte[] bytes = Convert.FromBase64String(parts[1]);
                if (bytes.Length > 4096)
                {
                    return false;
                }

                errorMessage = Encoding.UTF8.GetString(bytes);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string[] Split(string message, int expectedParts)
        {
            if (string.IsNullOrEmpty(message) || message.Length > 8192)
            {
                return null;
            }

            string[] parts = message.Split(new[] { '|' }, expectedParts);
            return parts.Length == expectedParts ? parts : null;
        }
    }
}
