using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace QuickWindowCapture
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly EventWaitHandle showSettingsEvent;
        private readonly NotifyIcon trayIcon;
        private readonly KeyboardHook keyboardHook;
        private readonly IconResource iconResource;
        private readonly CaptureSound captureSound;
        private readonly Control dispatcher;
        private readonly System.Windows.Forms.Timer eventTimer;
        private readonly object captureSignalLock = new object();
        private RawInputMonitor rawInputMonitor;
        private AppSettings settings;
        private SettingsForm settingsForm;
        private GpuCaptureService gpuCapture;
        private int pendingSaves;
        private long lastCaptureSignalTimestamp;
        private bool exiting;

        internal TrayApplicationContext(bool showSettingsOnStart, EventWaitHandle showEvent)
        {
            showSettingsEvent = showEvent;
            settings = AppSettings.Load();
            iconResource = IconResource.Create();
            captureSound = new CaptureSound();
            ConfigureGpuCapture();

            dispatcher = new Control();
            dispatcher.CreateControl();

            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem statusItem = new ToolStripMenuItem("단축키: " + settings.GetHotkeyDisplayName());
            statusItem.Enabled = false;
            statusItem.Name = "HotkeyStatus";
            menu.Items.Add(statusItem);
            ToolStripMenuItem modeItem = new ToolStripMenuItem("방식: " + settings.GetCaptureModeDisplayName());
            modeItem.Enabled = false;
            modeItem.Name = "CaptureModeStatus";
            menu.Items.Add(modeItem);
            ToolStripMenuItem privilegeItem = new ToolStripMenuItem(
                "권한: " + (PrivilegeManager.IsAdministrator() ? "관리자" : "일반"));
            privilegeItem.Enabled = false;
            privilegeItem.Name = "PrivilegeStatus";
            menu.Items.Add(privilegeItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("설정 열기", null, delegate { ShowSettings(); });
            menu.Items.Add("저장 폴더 열기", null, delegate { OpenSaveFolder(); });
            ToolStripMenuItem elevateItem = new ToolStripMenuItem(
                "관리자 권한으로 다시 시작",
                null,
                delegate { RestartAsAdministrator(); });
            elevateItem.Enabled = !PrivilegeManager.IsAdministrator();
            menu.Items.Add(elevateItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("종료", null, delegate { ExitApplication(); });

            trayIcon = new NotifyIcon
            {
                Icon = iconResource.Icon,
                Text = BuildTrayText(settings),
                ContextMenuStrip = menu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate { ShowSettings(); };

            keyboardHook = new KeyboardHook(settings);
            keyboardHook.CaptureRequested += QueueCapture;

            Exception hookError = null;
            try
            {
                keyboardHook.Start();
            }
            catch (Exception exception)
            {
                hookError = exception;
            }

            Exception rawInputError = null;
            try
            {
                rawInputMonitor = new RawInputMonitor(settings);
                rawInputMonitor.CaptureRequested += QueueCapture;
            }
            catch (Exception exception)
            {
                rawInputError = exception;
            }

            if (hookError != null && rawInputError != null)
            {
                MessageBox.Show(
                    "캡처 단축키를 시작하지 못했습니다.\r\n\r\n" +
                    hookError.Message + "\r\n" + rawInputError.Message,
                    "빠른 창 캡처",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }

            eventTimer = new System.Windows.Forms.Timer();
            eventTimer.Interval = 300;
            eventTimer.Tick += EventTimerTick;
            eventTimer.Start();

            if (showSettingsOnStart)
            {
                ShowSettings();
            }
        }

        private static string BuildTrayText(AppSettings value)
        {
            string text = "빠른 창 캡처 · " + value.GetHotkeyDisplayName();
            return text.Length <= 63 ? text : "빠른 창 캡처";
        }

        private void QueueCapture(IntPtr windowHandle)
        {
            long timestamp = Stopwatch.GetTimestamp();
            lock (captureSignalLock)
            {
                if (lastCaptureSignalTimestamp != 0)
                {
                    long elapsed = timestamp - lastCaptureSignalTimestamp;
                    if (elapsed >= 0 && elapsed * 1000 / Stopwatch.Frequency < 100)
                    {
                        return;
                    }
                }
                lastCaptureSignalTimestamp = timestamp;
            }

            try
            {
                dispatcher.BeginInvoke(new Action<IntPtr>(CaptureWindow), windowHandle);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void CaptureWindow(IntPtr windowHandle)
        {
            if (Interlocked.CompareExchange(ref pendingSaves, 0, 0) >= 3)
            {
                ShowCaptureError("이미지를 저장하는 중입니다. 잠시 후 다시 시도해 주세요.");
                return;
            }

            Bitmap bitmap = null;
            try
            {
                if (settings.CaptureMode == CaptureMode.Compatibility)
                {
                    bitmap = ScreenshotService.CaptureVisibleWindowBitmap(windowHandle);
                }
                else
                {
                    try
                    {
                        if (gpuCapture == null || !gpuCapture.IsAvailable)
                        {
                            throw new InvalidOperationException(
                                gpuCapture == null
                                    ? "고속 캡처 엔진이 시작되지 않았습니다."
                                    : gpuCapture.UnavailableReason);
                        }

                        bitmap = CaptureGpuWindowWithRecovery(windowHandle);
                    }
                    catch
                    {
                        if (settings.CaptureMode == CaptureMode.HighSpeed)
                        {
                            throw;
                        }

                        bitmap = ScreenshotService.CaptureVisibleWindowBitmap(windowHandle);
                    }
                }

                if (settings.CaptureSoundEnabled)
                {
                    captureSound.Play(
                        settings.CaptureSoundId,
                        settings.CaptureSoundOverlapEnabled);
                }

                QueueSave(bitmap, settings.SaveFolder);
                bitmap = null;
            }
            catch (Exception exception)
            {
                ShowCaptureError(exception.Message);
            }
            finally
            {
                if (bitmap != null)
                {
                    bitmap.Dispose();
                }
            }
        }

        private void QueueSave(Bitmap bitmap, string saveFolder)
        {
            Interlocked.Increment(ref pendingSaves);
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    ScreenshotService.SaveBitmap(bitmap, saveFolder);
                }
                catch (Exception exception)
                {
                    BeginOnUi(delegate { ShowCaptureError(exception.Message); });
                }
                finally
                {
                    bitmap.Dispose();
                    Interlocked.Decrement(ref pendingSaves);
                }
            });
        }

        private Bitmap CaptureGpuWindowWithRecovery(IntPtr windowHandle)
        {
            try
            {
                return gpuCapture.CaptureWindow(windowHandle);
            }
            catch (GpuCaptureResetRequiredException)
            {
                // Full-screen, resolution, HDR and monitor changes can invalidate
                // Desktop Duplication. Recreate it immediately so the next frame
                // does not require restarting the tray application.
                ConfigureGpuCapture();
                if (gpuCapture == null || !gpuCapture.IsAvailable)
                {
                    throw new InvalidOperationException(
                        gpuCapture == null
                            ? "고속 캡처 엔진을 다시 시작하지 못했습니다."
                            : gpuCapture.UnavailableReason);
                }

                return gpuCapture.CaptureWindow(windowHandle);
            }
        }

        private void ShowCaptureError(string message)
        {
            trayIcon.BalloonTipIcon = ToolTipIcon.Error;
            trayIcon.BalloonTipTitle = "캡처하지 못했습니다";
            trayIcon.BalloonTipText = message;
            trayIcon.ShowBalloonTip(2600);
        }

        private void BeginOnUi(MethodInvoker action)
        {
            try
            {
                dispatcher.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void ShowSettings()
        {
            if (settingsForm == null || settingsForm.IsDisposed)
            {
                settingsForm = new SettingsForm(
                    settings,
                    iconResource.Icon,
                    ApplySettings,
                    captureSound.Play,
                    captureSound.Stop);
            }
            else
            {
                settingsForm.UpdateCurrentSettings(settings);
            }

            if (!settingsForm.Visible)
            {
                settingsForm.Show();
            }

            if (settingsForm.WindowState == FormWindowState.Minimized)
            {
                settingsForm.WindowState = FormWindowState.Normal;
            }

            settingsForm.BringToFront();
            settingsForm.Activate();
            NativeMethods.SetForegroundWindow(settingsForm.Handle);
        }

        private void ApplySettings(AppSettings updatedSettings)
        {
            bool shouldRestartElevated = updatedSettings.RunAsAdministrator &&
                !PrivilegeManager.IsAdministrator();
            settings = updatedSettings.Copy();
            keyboardHook.UpdateSettings(settings);
            if (rawInputMonitor != null)
            {
                rawInputMonitor.UpdateSettings(settings);
            }
            lock (captureSignalLock)
            {
                lastCaptureSignalTimestamp = 0;
            }
            ConfigureGpuCapture();
            trayIcon.Text = BuildTrayText(settings);

            ToolStripItem status = trayIcon.ContextMenuStrip.Items["HotkeyStatus"];
            if (status != null)
            {
                status.Text = "단축키: " + settings.GetHotkeyDisplayName();
            }

            ToolStripItem modeStatus = trayIcon.ContextMenuStrip.Items["CaptureModeStatus"];
            if (modeStatus != null)
            {
                modeStatus.Text = "방식: " + settings.GetCaptureModeDisplayName();
            }

            if (shouldRestartElevated)
            {
                BeginOnUi(RestartAsAdministrator);
            }
        }

        private void RestartAsAdministrator()
        {
            if (PrivilegeManager.IsAdministrator())
            {
                return;
            }

            string errorMessage;
            if (PrivilegeManager.TryRestartAsAdministrator(true, out errorMessage))
            {
                ExitApplication();
                return;
            }

            MessageBox.Show(
                "관리자 권한으로 다시 시작하지 못했습니다.\r\n\r\n" + errorMessage,
                "빠른 창 캡처",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void ConfigureGpuCapture()
        {
            if (gpuCapture != null)
            {
                gpuCapture.Dispose();
                gpuCapture = null;
            }

            if (settings.CaptureMode != CaptureMode.Compatibility)
            {
                gpuCapture = new GpuCaptureService();
            }
        }

        private void OpenSaveFolder()
        {
            try
            {
                Directory.CreateDirectory(settings.SaveFolder);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + settings.SaveFolder + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "저장 폴더를 열지 못했습니다.\r\n\r\n" + exception.Message,
                    "빠른 창 캡처",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void EventTimerTick(object sender, EventArgs eventArgs)
        {
            if (showSettingsEvent.WaitOne(0))
            {
                ShowSettings();
            }
        }

        private void ExitApplication()
        {
            if (exiting)
            {
                return;
            }

            exiting = true;
            eventTimer.Stop();
            keyboardHook.Dispose();
            if (rawInputMonitor != null)
            {
                rawInputMonitor.Dispose();
                rawInputMonitor = null;
            }
            if (gpuCapture != null)
            {
                gpuCapture.Dispose();
                gpuCapture = null;
            }
            trayIcon.Visible = false;

            if (settingsForm != null && !settingsForm.IsDisposed)
            {
                settingsForm.CloseForExit();
                settingsForm.Dispose();
            }

            trayIcon.Dispose();
            dispatcher.Dispose();
            captureSound.Dispose();
            iconResource.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !exiting)
            {
                ExitApplication();
            }

            base.Dispose(disposing);
        }
    }
}
