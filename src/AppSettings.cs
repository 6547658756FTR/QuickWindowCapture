using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace QuickWindowCapture
{
    [Flags]
    internal enum HotkeyModifiers
    {
        None = 0,
        Control = 1,
        Alt = 2,
        Shift = 4,
        Windows = 8
    }

    internal enum CaptureMode
    {
        Automatic = 0,
        Compatibility = 1,
        HighSpeed = 2
    }

    internal sealed class AppSettings
    {
        internal Keys CaptureKey { get; set; }
        internal HotkeyModifiers Modifiers { get; set; }
        internal string SaveFolder { get; set; }
        internal CaptureMode CaptureMode { get; set; }
        internal bool CaptureSoundEnabled { get; set; }
        internal string CaptureSoundId { get; set; }
        internal bool CaptureSoundOverlapEnabled { get; set; }
        internal bool RunAsAdministrator { get; set; }

        internal static string SettingsFolder
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "QuickWindowCapture");
            }
        }

        private static string SettingsFile
        {
            get { return Path.Combine(SettingsFolder, "settings.ini"); }
        }

        internal static AppSettings CreateDefault()
        {
            return new AppSettings
            {
                CaptureKey = Keys.PrintScreen,
                Modifiers = HotkeyModifiers.None,
                CaptureMode = CaptureMode.Automatic,
                CaptureSoundEnabled = true,
                CaptureSoundId = CaptureSound.DefaultOptionId,
                CaptureSoundOverlapEnabled = false,
                RunAsAdministrator = false,
                SaveFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    "QuickWindowCapture")
            };
        }

        internal static AppSettings Load()
        {
            AppSettings settings = CreateDefault();

            try
            {
                if (!File.Exists(SettingsFile))
                {
                    return settings;
                }

                Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                {
                    int separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }

                    values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1);
                }

                int keyValue;
                if (values.ContainsKey("CaptureKey") &&
                    int.TryParse(values["CaptureKey"], NumberStyles.Integer, CultureInfo.InvariantCulture, out keyValue) &&
                    Enum.IsDefined(typeof(Keys), keyValue))
                {
                    settings.CaptureKey = (Keys)keyValue;
                }

                int modifiersValue;
                if (values.ContainsKey("Modifiers") &&
                    int.TryParse(values["Modifiers"], NumberStyles.Integer, CultureInfo.InvariantCulture, out modifiersValue))
                {
                    settings.Modifiers = (HotkeyModifiers)modifiersValue;
                }

                if (values.ContainsKey("SaveFolder") && !string.IsNullOrWhiteSpace(values["SaveFolder"]))
                {
                    settings.SaveFolder = values["SaveFolder"];
                }

                int captureModeValue;
                if (values.ContainsKey("CaptureMode") &&
                    int.TryParse(values["CaptureMode"], NumberStyles.Integer, CultureInfo.InvariantCulture, out captureModeValue) &&
                    Enum.IsDefined(typeof(CaptureMode), captureModeValue))
                {
                    settings.CaptureMode = (CaptureMode)captureModeValue;
                }

                bool soundEnabled;
                if (values.ContainsKey("CaptureSoundEnabled") &&
                    bool.TryParse(values["CaptureSoundEnabled"], out soundEnabled))
                {
                    settings.CaptureSoundEnabled = soundEnabled;
                }

                if (values.ContainsKey("CaptureSoundId") &&
                    CaptureSound.IsValidOptionId(values["CaptureSoundId"]))
                {
                    settings.CaptureSoundId = values["CaptureSoundId"];
                }

                bool soundOverlapEnabled;
                if (values.ContainsKey("CaptureSoundOverlapEnabled") &&
                    bool.TryParse(values["CaptureSoundOverlapEnabled"], out soundOverlapEnabled))
                {
                    settings.CaptureSoundOverlapEnabled = soundOverlapEnabled;
                }

                bool runAsAdministrator;
                if (values.ContainsKey("RunAsAdministrator") &&
                    bool.TryParse(values["RunAsAdministrator"], out runAsAdministrator))
                {
                    settings.RunAsAdministrator = runAsAdministrator;
                }
            }
            catch
            {
                return CreateDefault();
            }

            return settings;
        }

        internal void Save()
        {
            Directory.CreateDirectory(SettingsFolder);
            string[] lines =
            {
                "CaptureKey=" + ((int)CaptureKey).ToString(CultureInfo.InvariantCulture),
                "Modifiers=" + ((int)Modifiers).ToString(CultureInfo.InvariantCulture),
                "CaptureMode=" + ((int)CaptureMode).ToString(CultureInfo.InvariantCulture),
                "CaptureSoundEnabled=" + CaptureSoundEnabled.ToString(CultureInfo.InvariantCulture),
                "CaptureSoundId=" + CaptureSoundId,
                "CaptureSoundOverlapEnabled=" + CaptureSoundOverlapEnabled.ToString(CultureInfo.InvariantCulture),
                "RunAsAdministrator=" + RunAsAdministrator.ToString(CultureInfo.InvariantCulture),
                "SaveFolder=" + SaveFolder
            };

            File.WriteAllLines(SettingsFile, lines, new UTF8Encoding(false));
        }

        internal AppSettings Copy()
        {
            return new AppSettings
            {
                CaptureKey = CaptureKey,
                Modifiers = Modifiers,
                CaptureMode = CaptureMode,
                CaptureSoundEnabled = CaptureSoundEnabled,
                CaptureSoundId = CaptureSoundId,
                CaptureSoundOverlapEnabled = CaptureSoundOverlapEnabled,
                RunAsAdministrator = RunAsAdministrator,
                SaveFolder = SaveFolder
            };
        }

        internal string GetHotkeyDisplayName()
        {
            List<string> parts = new List<string>();
            if ((Modifiers & HotkeyModifiers.Control) != 0) parts.Add("Ctrl");
            if ((Modifiers & HotkeyModifiers.Alt) != 0) parts.Add("Alt");
            if ((Modifiers & HotkeyModifiers.Shift) != 0) parts.Add("Shift");
            if ((Modifiers & HotkeyModifiers.Windows) != 0) parts.Add("Win");
            parts.Add(KeyNames.ToDisplayName(CaptureKey));
            return string.Join(" + ", parts.ToArray());
        }

        internal string GetCaptureModeDisplayName()
        {
            if (CaptureMode == CaptureMode.Compatibility) return "일반 모드 고정";
            if (CaptureMode == CaptureMode.HighSpeed) return "고속 게임 모드 고정";
            return "자동 선택";
        }
    }

    internal static class KeyNames
    {
        internal static string ToDisplayName(Keys key)
        {
            if (key == Keys.PrintScreen) return "Print Screen";
            if (key == Keys.PageUp) return "Page Up";
            if (key == Keys.PageDown) return "Page Down";
            if (key == Keys.Scroll) return "Scroll Lock";
            if (key == Keys.Oemtilde) return "`";
            if (key >= Keys.D0 && key <= Keys.D9) return ((int)(key - Keys.D0)).ToString(CultureInfo.InvariantCulture);
            return key.ToString();
        }
    }
}
