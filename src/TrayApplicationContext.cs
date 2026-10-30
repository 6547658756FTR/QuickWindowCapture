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
        private CaptureWorker captureWorker;
        private volatile int captureWorkerGeneration;
        private int recentCaptureTimeouts;
        private long lastCaptureTimeoutTimestamp;
        private int pendingSaves;
        private long lastCaptureSignalTimestamp;
        private long lastQueueDropLogTimestamp;
        private volatile bool exiting;

        private const int CaptureWorkerTimeoutMilliseconds = 1500;
        private const int RepeatedTimeoutWindowMilliseconds = 60000;

        internal TrayApplicationContext(bool showSettingsOnStart, EventWaitHandle showEvent)
        {
            showSettingsEvent = showEvent;
            settings = AppSettings.Load();
            iconResource = IconResource.Create();
            captureSound = new CaptureSound();

            dispatcher = new Control();
            dispatcher.CreateControl();
            ReplaceCaptureWorker(false, null);
            DiagnosticLog.Write("빠른 창 캡처 1.6.4 시작");

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

            CaptureWorker worker = captureWorker;
            if (worker == null || !worker.TryEnqueue(windowHandle, settings))
            {
                LogDroppedCaptureRequest(timestamp);
            }
        }

        private void LogDroppedCaptureRequest(long timestamp)
        {
            lock (captureSignalLock)
            {
                if (lastQueueDropLogTimestamp != 0)
                {
                    long elapsed = timestamp - lastQueueDropLogTimestamp;
                    if (elapsed >= 0 && elapsed * 1000 / Stopwatch.Frequency < 5000)
                    {
                        return;
                    }
                }

                lastQueueDropLogTimestamp = timestamp;
            }

            DiagnosticLog.Write("캡처 요청이 대기열 제한으로 생략되었습니다.");
        }

        private void CaptureCompleted(
            int workerGeneration,
            Bitmap bitmap,
            AppSettings requestSettings,
            long elapsedMilliseconds)
        {
            if (exiting || workerGeneration != captureWorkerGeneration)
            {
                bitmap.Dispose();
                return;
            }

            if (elapsedMilliseconds >= 250)
            {
                DiagnosticLog.Write("느린 캡처 감지: " + elapsedMilliseconds + "ms");
            }

            int saveCount = Interlocked.Increment(ref pendingSaves);
            if (saveCount > 3)
            {
                Interlocked.Decrement(ref pendingSaves);
                bitmap.Dispose();
                BeginOnUi(delegate
                {
                    ShowCaptureError("이미지를 저장하는 중입니다. 잠시 후 다시 시도해 주세요.");
                });
                return;
            }

            if (requestSettings.CaptureSoundEnabled)
            {
                BeginOnUi(delegate
                {
                    if (!exiting)
                    {
                        captureSound.Play(
                            requestSettings.CaptureSoundId,
                            requestSettings.CaptureSoundOverlapEnabled);
                    }
                });
            }

            QueueSave(bitmap, requestSettings.SaveFolder);
        }

        private void CaptureFailed(int workerGeneration, string message)
        {
            if (exiting || workerGeneration != captureWorkerGeneration)
            {
                return;
            }

            DiagnosticLog.Write("캡처 실패: " + message);
            BeginOnUi(delegate
            {
                if (!exiting && workerGeneration == captureWorkerGeneration)
                {
                    ShowCaptureError(message);
                }
            });
        }

        private void QueueSave(Bitmap bitmap, string saveFolder)
        {
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
            CaptureMode previousMode = settings.CaptureMode;
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
            if (previousMode != settings.CaptureMode)
            {
                recentCaptureTimeouts = 0;
                lastCaptureTimeoutTimestamp = 0;
                ReplaceCaptureWorker(false, null);
            }
            else if (captureWorker != null)
            {
                captureWorker.UpdateSettings(settings);
            }
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

        private void ReplaceCaptureWorker(bool forceCompatibility, string reason)
        {
            CaptureWorker previous = captureWorker;
            if (previous != null)
            {
                previous.Retire();
            }

            captureWorkerGeneration++;
            captureWorker = new CaptureWorker(
                captureWorkerGeneration,
                settings,
                forceCompatibility,
                CaptureCompleted,
                CaptureFailed);

            if (!string.IsNullOrEmpty(reason))
            {
                DiagnosticLog.Write(reason + " 새 작업자=" + captureWorkerGeneration +
                    (forceCompatibility ? " 자동 모드 일반 캡처 전환" : " GPU 엔진 재시작"));
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

            MonitorCaptureWorker();
        }

        private void MonitorCaptureWorker()
        {
            CaptureWorker worker = captureWorker;
            if (worker == null || !worker.HasTimedOut(CaptureWorkerTimeoutMilliseconds))
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (lastCaptureTimeoutTimestamp == 0 || now < lastCaptureTimeoutTimestamp ||
                (now - lastCaptureTimeoutTimestamp) * 1000 / Stopwatch.Frequency >
                    RepeatedTimeoutWindowMilliseconds)
            {
                recentCaptureTimeouts = 0;
            }

            recentCaptureTimeouts++;
            lastCaptureTimeoutTimestamp = now;
            bool forceCompatibility = settings.CaptureMode == CaptureMode.Automatic &&
                recentCaptureTimeouts >= 2;

            ReplaceCaptureWorker(
                forceCompatibility,
                "캡처 작업자가 " + CaptureWorkerTimeoutMilliseconds + "ms 동안 응답하지 않았습니다.");

            ShowCaptureError(
                forceCompatibility
                    ? "고속 캡처가 반복해서 지연되어 이번 실행에서는 일반 방식으로 자동 전환했습니다."
                    : "캡처 엔진 응답이 지연되어 자동으로 다시 시작했습니다. 이번 한 장은 저장되지 않았을 수 있습니다.");
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
            if (captureWorker != null)
            {
                captureWorker.Dispose();
                captureWorker = null;
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
