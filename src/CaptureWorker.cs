using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;

namespace QuickWindowCapture
{
    // Owns all potentially blocking screen-capture work. The tray message loop,
    // keyboard hook and Raw Input window only enqueue requests and never wait for
    // Direct3D or Graphics.CopyFromScreen.
    internal sealed class CaptureWorker : IDisposable
    {
        private const int MaximumPendingRequests = 2;

        private sealed class CaptureRequest
        {
            internal IntPtr WindowHandle;
            internal AppSettings Settings;
        }

        private readonly object syncRoot = new object();
        private readonly Queue<CaptureRequest> requests = new Queue<CaptureRequest>();
        private readonly AutoResetEvent workSignal = new AutoResetEvent(false);
        private readonly Thread workerThread;
        private readonly Action<int, Bitmap, AppSettings, long> captureSucceeded;
        private readonly Action<int, string> captureFailed;
        private readonly Func<IntPtr, AppSettings, Bitmap> captureOverride;
        private readonly bool forceCompatibilityForAutomatic;
        private readonly int generation;
        private AppSettings desiredSettings;
        private GpuCaptureService gpuCapture;
        private int desiredSettingsRevision = 1;
        private int appliedSettingsRevision;
        private int busy;
        private int requestSeen;
        private long busySinceTimestamp;
        private volatile bool acceptingResults = true;
        private volatile bool stopping;

        internal CaptureWorker(
            int workerGeneration,
            AppSettings initialSettings,
            bool forceCompatibility,
            Action<int, Bitmap, AppSettings, long> succeeded,
            Action<int, string> failed)
            : this(
                workerGeneration,
                initialSettings,
                forceCompatibility,
                succeeded,
                failed,
                null)
        {
        }

        // The optional operation is used by the isolation test to simulate a
        // graphics driver call that never returns. Production always passes null.
        internal CaptureWorker(
            int workerGeneration,
            AppSettings initialSettings,
            bool forceCompatibility,
            Action<int, Bitmap, AppSettings, long> succeeded,
            Action<int, string> failed,
            Func<IntPtr, AppSettings, Bitmap> operationOverride)
        {
            generation = workerGeneration;
            desiredSettings = initialSettings.Copy();
            forceCompatibilityForAutomatic = forceCompatibility;
            captureSucceeded = succeeded;
            captureFailed = failed;
            captureOverride = operationOverride;

            workerThread = new Thread(Run);
            workerThread.IsBackground = true;
            workerThread.Name = "QuickWindowCapture Capture Worker " + generation;
            workerThread.SetApartmentState(ApartmentState.MTA);
            workerThread.Start();
        }

        internal bool TryEnqueue(IntPtr windowHandle, AppSettings requestSettings)
        {
            lock (syncRoot)
            {
                if (stopping || requests.Count >= MaximumPendingRequests)
                {
                    return false;
                }

                requests.Enqueue(new CaptureRequest
                {
                    WindowHandle = windowHandle,
                    Settings = requestSettings.Copy()
                });
                Interlocked.Exchange(ref requestSeen, 1);
            }

            workSignal.Set();
            return true;
        }

        internal void UpdateSettings(AppSettings updatedSettings)
        {
            lock (syncRoot)
            {
                if (stopping)
                {
                    return;
                }

                desiredSettings = updatedSettings.Copy();
                desiredSettingsRevision++;
            }

            workSignal.Set();
        }

        internal bool HasTimedOut(int timeoutMilliseconds)
        {
            if (Interlocked.CompareExchange(ref busy, 0, 0) == 0 ||
                Interlocked.CompareExchange(ref requestSeen, 0, 0) == 0)
            {
                return false;
            }

            long started = Interlocked.Read(ref busySinceTimestamp);
            long now = Stopwatch.GetTimestamp();
            if (started <= 0 || now < started)
            {
                return false;
            }

            return (now - started) * 1000 / Stopwatch.Frequency >= timeoutMilliseconds;
        }

        internal void Retire()
        {
            lock (syncRoot)
            {
                if (stopping)
                {
                    return;
                }

                acceptingResults = false;
                stopping = true;
                requests.Clear();
            }
            workSignal.Set();
        }

        private void Run()
        {
            try
            {
                while (!stopping)
                {
                    CaptureRequest request;
                    AppSettings settingsToApply;
                    int settingsRevision;
                    lock (syncRoot)
                    {
                        request = requests.Count > 0 ? requests.Dequeue() : null;
                        settingsToApply = desiredSettings.Copy();
                        settingsRevision = desiredSettingsRevision;
                    }

                    if (settingsRevision != appliedSettingsRevision)
                    {
                        BeginBusy();
                        try
                        {
                            ConfigureGpuCapture(settingsToApply.CaptureMode);
                            appliedSettingsRevision = settingsRevision;
                        }
                        catch (Exception exception)
                        {
                            DiagnosticLog.Write(
                                "캡처 작업자 " + generation + " 엔진 준비 실패: " + exception.Message);
                        }
                        finally
                        {
                            EndBusy();
                        }
                    }

                    if (stopping)
                    {
                        break;
                    }

                    if (request == null)
                    {
                        workSignal.WaitOne();
                        continue;
                    }

                    ProcessRequest(request);
                }
            }
            finally
            {
                DisposeGpuCapture();
                workSignal.Dispose();
            }
        }

        private void ProcessRequest(CaptureRequest request)
        {
            Bitmap bitmap = null;
            long started = Stopwatch.GetTimestamp();
            BeginBusy();
            try
            {
                bitmap = captureOverride == null
                    ? CaptureWindow(request.WindowHandle, request.Settings)
                    : captureOverride(request.WindowHandle, request.Settings);
                long elapsedMilliseconds = ElapsedMilliseconds(started);

                if (!acceptingResults || stopping)
                {
                    bitmap.Dispose();
                    bitmap = null;
                    return;
                }

                Action<int, Bitmap, AppSettings, long> succeeded = captureSucceeded;
                if (succeeded != null)
                {
                    succeeded(generation, bitmap, request.Settings, elapsedMilliseconds);
                    bitmap = null;
                }
            }
            catch (Exception exception)
            {
                if (acceptingResults && !stopping)
                {
                    Action<int, string> failed = captureFailed;
                    if (failed != null)
                    {
                        failed(generation, exception.Message);
                    }
                }
            }
            finally
            {
                if (bitmap != null)
                {
                    bitmap.Dispose();
                }
                EndBusy();
            }
        }

        private Bitmap CaptureWindow(IntPtr windowHandle, AppSettings requestSettings)
        {
            CaptureMode mode = requestSettings.CaptureMode;
            if (forceCompatibilityForAutomatic && mode == CaptureMode.Automatic)
            {
                mode = CaptureMode.Compatibility;
            }

            if (mode == CaptureMode.Compatibility)
            {
                return ScreenshotService.CaptureVisibleWindowBitmap(windowHandle);
            }

            try
            {
                EnsureGpuCapture();
                return CaptureGpuWindowWithRecovery(windowHandle);
            }
            catch
            {
                if (mode == CaptureMode.HighSpeed)
                {
                    throw;
                }

                return ScreenshotService.CaptureVisibleWindowBitmap(windowHandle);
            }
        }

        private Bitmap CaptureGpuWindowWithRecovery(IntPtr windowHandle)
        {
            try
            {
                return gpuCapture.CaptureWindow(windowHandle);
            }
            catch (GpuCaptureResetRequiredException)
            {
                DiagnosticLog.Write("GPU 캡처 연결을 다시 생성합니다. 작업자=" + generation);
                DisposeGpuCapture();
                EnsureGpuCapture();
                return gpuCapture.CaptureWindow(windowHandle);
            }
        }

        private void ConfigureGpuCapture(CaptureMode mode)
        {
            bool needsGpu = mode != CaptureMode.Compatibility &&
                !(forceCompatibilityForAutomatic && mode == CaptureMode.Automatic);
            if (needsGpu)
            {
                EnsureGpuCapture();
            }
            else
            {
                DisposeGpuCapture();
            }
        }

        private void EnsureGpuCapture()
        {
            if (gpuCapture == null)
            {
                gpuCapture = new GpuCaptureService();
            }

            if (!gpuCapture.IsAvailable)
            {
                throw new InvalidOperationException(
                    gpuCapture.UnavailableReason ?? "고속 캡처 엔진을 사용할 수 없습니다.");
            }
        }

        private void DisposeGpuCapture()
        {
            if (gpuCapture != null)
            {
                gpuCapture.Dispose();
                gpuCapture = null;
            }
        }

        private void BeginBusy()
        {
            Interlocked.Exchange(ref busySinceTimestamp, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref busy, 1);
        }

        private void EndBusy()
        {
            Interlocked.Exchange(ref busy, 0);
            Interlocked.Exchange(ref busySinceTimestamp, 0);
        }

        private static long ElapsedMilliseconds(long started)
        {
            long now = Stopwatch.GetTimestamp();
            if (now < started)
            {
                return 0;
            }
            return (now - started) * 1000 / Stopwatch.Frequency;
        }

        public void Dispose()
        {
            Retire();
        }
    }
}
