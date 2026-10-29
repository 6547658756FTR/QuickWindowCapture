using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace QuickWindowCapture
{
    internal sealed class SettingsForm : Form
    {
        private sealed class KeyOption
        {
            internal Keys Key { get; private set; }
            internal string Name { get; private set; }

            internal KeyOption(Keys key, string name)
            {
                Key = key;
                Name = name;
            }

            public override string ToString()
            {
                return Name;
            }
        }

        private readonly Action<AppSettings> saved;
        private readonly Func<string, bool, bool> soundPreview;
        private readonly Action soundStop;
        private AppSettings currentSettings;
        private readonly CheckBox controlCheckBox;
        private readonly CheckBox altCheckBox;
        private readonly CheckBox shiftCheckBox;
        private readonly CheckBox windowsCheckBox;
        private readonly ComboBox keyComboBox;
        private readonly Label previewLabel;
        private readonly ComboBox modeComboBox;
        private readonly CheckBox captureSoundCheckBox;
        private readonly CheckBox overlapSoundCheckBox;
        private readonly ListBox soundListBox;
        private readonly Button previewSoundButton;
        private readonly TextBox folderTextBox;
        private readonly CheckBox startupCheckBox;
        private readonly CheckBox elevatedModeCheckBox;
        private bool allowClose;

        internal SettingsForm(
            AppSettings settings,
            Icon appIcon,
            Action<AppSettings> onSaved,
            Func<string, bool, bool> onSoundPreview,
            Action onSoundStop)
        {
            currentSettings = settings.Copy();
            saved = onSaved;
            soundPreview = onSoundPreview;
            soundStop = onSoundStop;

            SuspendLayout();

            Text = "빠른 창 캡처 설정";
            Icon = appIcon;
            Font = new Font("맑은 고딕", 9F);
            BackColor = Color.FromArgb(248, 250, 252);
            ForeColor = Color.FromArgb(30, 41, 59);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(650, 672);

            Panel header = new Panel
            {
                BackColor = Color.White,
                Dock = DockStyle.Top,
                Height = 72
            };
            Controls.Add(header);

            Label title = new Label
            {
                AutoSize = true,
                Font = new Font("맑은 고딕", 16F, FontStyle.Bold),
                ForeColor = Color.FromArgb(15, 23, 42),
                Location = new Point(26, 9),
                Text = "빠른 창 캡처"
            };
            header.Controls.Add(title);

            Label subtitle = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(29, 43),
                Text = "단축키를 누르는 순간, 현재 활성 창을 PNG로 저장합니다."
            };
            header.Controls.Add(subtitle);

            Label hotkeyTitle = SectionTitle("캡처 단축키", 28, 91);
            Controls.Add(hotkeyTitle);

            controlCheckBox = ModifierCheckBox("Ctrl", 30, 121);
            altCheckBox = ModifierCheckBox("Alt", 103, 121);
            shiftCheckBox = ModifierCheckBox("Shift", 167, 121);
            windowsCheckBox = ModifierCheckBox("Win", 243, 121);
            Controls.Add(controlCheckBox);
            Controls.Add(altCheckBox);
            Controls.Add(shiftCheckBox);
            Controls.Add(windowsCheckBox);

            keyComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(358, 118),
                Size = new Size(262, 26)
            };
            keyComboBox.Items.AddRange(CreateKeyOptions().ToArray());
            Controls.Add(keyComboBox);

            previewLabel = new Label
            {
                AutoSize = false,
                BackColor = Color.FromArgb(239, 246, 255),
                ForeColor = Color.FromArgb(29, 78, 216),
                Location = new Point(30, 154),
                Padding = new Padding(11, 7, 8, 0),
                Size = new Size(590, 34),
                TextAlign = ContentAlignment.TopLeft
            };
            Controls.Add(previewLabel);

            Label modeTitle = SectionTitle("캡처 방식", 28, 207);
            Controls.Add(modeTitle);

            modeComboBox = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(30, 236),
                Size = new Size(258, 26)
            };
            modeComboBox.Items.AddRange(new object[]
            {
                "자동 선택 (권장)",
                "일반 모드로 고정",
                "고속 게임 모드로 고정"
            });
            Controls.Add(modeComboBox);

            Label modeHint = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(30, 272),
                Size = new Size(258, 68),
                Text = "자동은 GPU 고속 캡처를 먼저 사용하고,\r\n사용할 수 없을 때 일반 방식으로\r\n전환합니다."
            };
            Controls.Add(modeHint);

            Label soundTitle = SectionTitle("캡처 효과음", 318, 207);
            Controls.Add(soundTitle);

            captureSoundCheckBox = new CheckBox
            {
                AutoSize = true,
                Location = new Point(321, 236),
                Text = "캡처할 때 효과음 재생"
            };
            Controls.Add(captureSoundCheckBox);

            soundListBox = new ListBox
            {
                Location = new Point(320, 263),
                Size = new Size(215, 116),
                IntegralHeight = false
            };
            soundListBox.Items.AddRange(CaptureSound.GetOptions());
            Controls.Add(soundListBox);

            previewSoundButton = new Button
            {
                Text = "미리듣기",
                Location = new Point(544, 263),
                Size = new Size(76, 32),
                FlatStyle = FlatStyle.System
            };
            previewSoundButton.Click += PreviewSoundButtonClick;
            Controls.Add(previewSoundButton);

            overlapSoundCheckBox = new CheckBox
            {
                AutoSize = true,
                Location = new Point(321, 386),
                Text = "연속 캡처 시 효과음 겹쳐 재생"
            };
            Controls.Add(overlapSoundCheckBox);

            Label soundHint = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(320, 411),
                Size = new Size(300, 18),
                Text = "끄면 이전 효과음을 중지하고 새로 재생합니다."
            };
            Controls.Add(soundHint);

            Label folderTitle = SectionTitle("저장 폴더", 28, 438);
            Controls.Add(folderTitle);

            folderTextBox = new TextBox
            {
                Location = new Point(30, 467),
                Size = new Size(500, 25)
            };
            Controls.Add(folderTextBox);

            Button browseButton = new Button
            {
                Text = "찾아보기",
                Location = new Point(540, 464),
                Size = new Size(80, 30),
                FlatStyle = FlatStyle.System
            };
            browseButton.Click += BrowseButtonClick;
            Controls.Add(browseButton);

            Label startupTitle = SectionTitle("Windows 시작", 28, 503);
            Controls.Add(startupTitle);

            startupCheckBox = new CheckBox
            {
                AutoSize = true,
                Location = new Point(31, 533),
                Text = "로그인할 때 자동으로 실행하고 트레이에 숨기기"
            };
            Controls.Add(startupCheckBox);

            Label trayHint = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(360, 535),
                Text = "닫아도 트레이에서 계속 실행됩니다."
            };
            Controls.Add(trayHint);

            elevatedModeCheckBox = new CheckBox
            {
                AutoSize = true,
                Location = new Point(31, 565),
                Text = "관리자 권한 호환 모드"
            };
            Controls.Add(elevatedModeCheckBox);

            Label privilegeHint = new Label
            {
                AutoSize = false,
                ForeColor = Color.FromArgb(100, 116, 139),
                Location = new Point(31, 592),
                Size = new Size(589, 34),
                Text = PrivilegeManager.IsAdministrator()
                    ? "현재 관리자 권한으로 실행 중입니다. 해제하면 다음 실행부터 일반 권한으로 시작합니다."
                    : "켜면 저장 직후 UAC 확인 후 관리자 권한으로 다시 시작합니다. 자동 실행 시에도 UAC 확인이 표시됩니다."
            };
            Controls.Add(privilegeHint);

            Button cancelButton = new Button
            {
                Text = "취소",
                Location = new Point(442, 630),
                Size = new Size(82, 32)
            };
            cancelButton.Click += delegate
            {
                StopSoundPreview();
                LoadSettingsIntoControls();
                Hide();
            };
            Controls.Add(cancelButton);

            Button saveButton = new Button
            {
                Text = "저장하고 닫기",
                Location = new Point(532, 630),
                Size = new Size(104, 32),
                BackColor = Color.FromArgb(37, 99, 235),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            saveButton.FlatAppearance.BorderSize = 0;
            saveButton.Click += SaveButtonClick;
            Controls.Add(saveButton);
            AcceptButton = saveButton;
            CancelButton = cancelButton;

            controlCheckBox.CheckedChanged += HotkeyControlChanged;
            altCheckBox.CheckedChanged += HotkeyControlChanged;
            shiftCheckBox.CheckedChanged += HotkeyControlChanged;
            windowsCheckBox.CheckedChanged += HotkeyControlChanged;
            keyComboBox.SelectedIndexChanged += HotkeyControlChanged;
            captureSoundCheckBox.CheckedChanged += SoundEnabledChanged;
            overlapSoundCheckBox.CheckedChanged += SoundOverlapChanged;
            soundListBox.SelectedIndexChanged += SoundSelectionChanged;
            soundListBox.DoubleClick += PreviewSoundButtonClick;

            FormClosing += SettingsFormClosing;

            // 모든 좌표와 크기는 96 DPI 기준입니다. 명시적인 DPI 자동 배율을 사용해야
            // Windows의 125~200% 디스플레이 배율에서 글자와 컨트롤이 함께 커집니다.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            LoadSettingsIntoControls();
            ResumeLayout(false);
            PerformLayout();
        }

        internal void UpdateCurrentSettings(AppSettings settings)
        {
            StopSoundPreview();
            currentSettings = settings.Copy();
            LoadSettingsIntoControls();
        }

        internal void CloseForExit()
        {
            StopSoundPreview();
            allowClose = true;
            Close();
        }

        private static Label SectionTitle(string text, int x, int y)
        {
            return new Label
            {
                AutoSize = true,
                Font = new Font("맑은 고딕", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(51, 65, 85),
                Location = new Point(x, y),
                Text = text
            };
        }

        private static CheckBox ModifierCheckBox(string text, int x, int y)
        {
            return new CheckBox
            {
                AutoSize = true,
                Location = new Point(x, y),
                Text = text
            };
        }

        private static List<KeyOption> CreateKeyOptions()
        {
            List<KeyOption> options = new List<KeyOption>();
            options.Add(new KeyOption(Keys.PrintScreen, "Print Screen"));

            for (Keys key = Keys.F1; key <= Keys.F12; key++)
            {
                options.Add(new KeyOption(key, key.ToString()));
            }

            for (Keys key = Keys.A; key <= Keys.Z; key++)
            {
                options.Add(new KeyOption(key, key.ToString()));
            }

            for (Keys key = Keys.D0; key <= Keys.D9; key++)
            {
                options.Add(new KeyOption(key, KeyNames.ToDisplayName(key)));
            }

            options.Add(new KeyOption(Keys.Insert, "Insert"));
            options.Add(new KeyOption(Keys.Home, "Home"));
            options.Add(new KeyOption(Keys.End, "End"));
            options.Add(new KeyOption(Keys.PageUp, "Page Up"));
            options.Add(new KeyOption(Keys.PageDown, "Page Down"));
            options.Add(new KeyOption(Keys.Scroll, "Scroll Lock"));
            options.Add(new KeyOption(Keys.Pause, "Pause"));
            options.Add(new KeyOption(Keys.Oemtilde, "`"));
            return options;
        }

        private void LoadSettingsIntoControls()
        {
            controlCheckBox.Checked = (currentSettings.Modifiers & HotkeyModifiers.Control) != 0;
            altCheckBox.Checked = (currentSettings.Modifiers & HotkeyModifiers.Alt) != 0;
            shiftCheckBox.Checked = (currentSettings.Modifiers & HotkeyModifiers.Shift) != 0;
            windowsCheckBox.Checked = (currentSettings.Modifiers & HotkeyModifiers.Windows) != 0;

            for (int index = 0; index < keyComboBox.Items.Count; index++)
            {
                KeyOption option = (KeyOption)keyComboBox.Items[index];
                if (option.Key == currentSettings.CaptureKey)
                {
                    keyComboBox.SelectedIndex = index;
                    break;
                }
            }

            if (keyComboBox.SelectedIndex < 0)
            {
                keyComboBox.SelectedIndex = 0;
            }

            folderTextBox.Text = currentSettings.SaveFolder;
            captureSoundCheckBox.Checked = currentSettings.CaptureSoundEnabled;
            overlapSoundCheckBox.Checked = currentSettings.CaptureSoundOverlapEnabled;
            elevatedModeCheckBox.Checked = currentSettings.RunAsAdministrator;

            soundListBox.SelectedIndex = -1;
            for (int index = 0; index < soundListBox.Items.Count; index++)
            {
                CaptureSoundOption soundOption = (CaptureSoundOption)soundListBox.Items[index];
                if (string.Equals(soundOption.Id, currentSettings.CaptureSoundId, StringComparison.OrdinalIgnoreCase))
                {
                    soundListBox.SelectedIndex = index;
                    break;
                }
            }

            if (soundListBox.SelectedIndex < 0)
            {
                soundListBox.SelectedIndex = 0;
            }

            modeComboBox.SelectedIndex = (int)currentSettings.CaptureMode;
            if (modeComboBox.SelectedIndex < 0)
            {
                modeComboBox.SelectedIndex = 0;
            }
            try
            {
                startupCheckBox.Checked = StartupManager.IsEnabled();
            }
            catch
            {
                startupCheckBox.Checked = false;
            }

            UpdateHotkeyPreview();
            UpdateSoundControls();
        }

        private void SoundEnabledChanged(object sender, EventArgs eventArgs)
        {
            if (!captureSoundCheckBox.Checked)
            {
                StopSoundPreview();
            }

            UpdateSoundControls();
        }

        private void SoundSelectionChanged(object sender, EventArgs eventArgs)
        {
            StopSoundPreview();
            UpdateSoundControls();
        }

        private void SoundOverlapChanged(object sender, EventArgs eventArgs)
        {
            if (!overlapSoundCheckBox.Checked)
            {
                StopSoundPreview();
            }
        }

        private void UpdateSoundControls()
        {
            soundListBox.Enabled = captureSoundCheckBox.Checked;
            previewSoundButton.Enabled = captureSoundCheckBox.Checked && soundListBox.SelectedItem != null;
            overlapSoundCheckBox.Enabled = captureSoundCheckBox.Checked;
        }

        private void PreviewSoundButtonClick(object sender, EventArgs eventArgs)
        {
            CaptureSoundOption option = soundListBox.SelectedItem as CaptureSoundOption;
            if (!captureSoundCheckBox.Checked || option == null || soundPreview == null)
            {
                return;
            }

            if (!soundPreview(option.Id, overlapSoundCheckBox.Checked))
            {
                MessageBox.Show(
                    this,
                    "효과음을 재생하지 못했습니다. Windows 소리 출력 장치를 확인해 주세요.",
                    "빠른 창 캡처",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private void StopSoundPreview()
        {
            if (soundStop != null)
            {
                soundStop();
            }
        }

        private void HotkeyControlChanged(object sender, EventArgs eventArgs)
        {
            UpdateHotkeyPreview();
        }

        private void UpdateHotkeyPreview()
        {
            if (keyComboBox.SelectedItem == null)
            {
                return;
            }

            previewLabel.Text = "현재 단축키   " + CreateSettingsFromControls().GetHotkeyDisplayName();
        }

        private AppSettings CreateSettingsFromControls()
        {
            HotkeyModifiers modifiers = HotkeyModifiers.None;
            if (controlCheckBox.Checked) modifiers |= HotkeyModifiers.Control;
            if (altCheckBox.Checked) modifiers |= HotkeyModifiers.Alt;
            if (shiftCheckBox.Checked) modifiers |= HotkeyModifiers.Shift;
            if (windowsCheckBox.Checked) modifiers |= HotkeyModifiers.Windows;

            KeyOption option = keyComboBox.SelectedItem as KeyOption;
            CaptureSoundOption soundOption = soundListBox.SelectedItem as CaptureSoundOption;
            return new AppSettings
            {
                CaptureKey = option == null ? Keys.PrintScreen : option.Key,
                Modifiers = modifiers,
                CaptureMode = modeComboBox.SelectedIndex < 0
                    ? CaptureMode.Automatic
                    : (CaptureMode)modeComboBox.SelectedIndex,
                CaptureSoundEnabled = captureSoundCheckBox.Checked,
                CaptureSoundId = soundOption == null ? CaptureSound.DefaultOptionId : soundOption.Id,
                CaptureSoundOverlapEnabled = overlapSoundCheckBox.Checked,
                RunAsAdministrator = elevatedModeCheckBox.Checked,
                SaveFolder = folderTextBox.Text.Trim()
            };
        }

        private void BrowseButtonClick(object sender, EventArgs eventArgs)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "캡처 이미지를 저장할 폴더를 선택하세요.";
                dialog.ShowNewFolderButton = true;
                if (Directory.Exists(folderTextBox.Text))
                {
                    dialog.SelectedPath = folderTextBox.Text;
                }

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    folderTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void SaveButtonClick(object sender, EventArgs eventArgs)
        {
            AppSettings updated = CreateSettingsFromControls();
            if (string.IsNullOrWhiteSpace(updated.SaveFolder))
            {
                MessageBox.Show(this, "저장 폴더를 선택해 주세요.", "빠른 창 캡처", MessageBoxButtons.OK, MessageBoxIcon.Information);
                folderTextBox.Focus();
                return;
            }

            try
            {
                updated.SaveFolder = Path.GetFullPath(Environment.ExpandEnvironmentVariables(updated.SaveFolder));
                Directory.CreateDirectory(updated.SaveFolder);
                updated.Save();
                StartupManager.SetEnabled(startupCheckBox.Checked);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    this,
                    "설정을 저장하지 못했습니다.\r\n\r\n" + exception.Message,
                    "빠른 창 캡처",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            currentSettings = updated.Copy();
            if (saved != null)
            {
                saved(updated.Copy());
            }
            StopSoundPreview();
            Hide();
        }

        private void SettingsFormClosing(object sender, FormClosingEventArgs eventArgs)
        {
            if (!allowClose && eventArgs.CloseReason == CloseReason.UserClosing)
            {
                eventArgs.Cancel = true;
                StopSoundPreview();
                LoadSettingsIntoControls();
                Hide();
            }
        }
    }
}
