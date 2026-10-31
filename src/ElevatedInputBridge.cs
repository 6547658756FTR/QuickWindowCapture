using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace QuickWindowCapture
{
    internal sealed class ElevatedInputBridge : IDisposable
    {
        private const int ConnectionTimeoutMilliseconds = 10000;
        private readonly Action<IntPtr> captureRequested;
        private readonly Action<bool, string> stateChanged;
        private readonly Action<string> errorReported;
        private readonly object writerLock = new object();
        private readonly object settingsLock = new object();
        private NamedPipeServerStream pipe;
        private StreamWriter writer;
        private Process helperProcess;
        private Thread readerThread;
        private AppSettings settings;
        private volatile bool connected;
        private volatile bool disposed;

        internal ElevatedInputBridge(
            AppSettings initialSettings,
            Action<IntPtr> captureHandler,
            Action<bool, string> stateHandler,
            Action<string> errorHandler)
        {
            settings = initialSettings.Copy();
            captureRequested = captureHandler;
            stateChanged = stateHandler;
            errorReported = errorHandler;
        }

        internal bool IsConnected
        {
            get { return connected; }
        }

        internal bool Start(out string errorMessage)
        {
            errorMessage = null;
            if (disposed)
            {
                errorMessage = "입력 도우미 연결이 이미 종료되었습니다.";
                return false;
            }

            string pipeName = "QuickWindowCapture.Input." + Guid.NewGuid().ToString("N");
            try
            {
                PipeSecurity security = CreatePipeSecurity();
                pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    4096,
                    4096,
                    security);
            }
            catch (Exception exception)
            {
                errorMessage = "보안 입력 채널을 만들지 못했습니다. " + exception.Message;
                Dispose();
                return false;
            }

            if (!PrivilegeManager.TryStartElevatedInputHelper(
                pipeName,
                Process.GetCurrentProcess().Id,
                out helperProcess,
                out errorMessage))
            {
                Dispose();
                return false;
            }

            readerThread = new Thread(RunConnection);
            readerThread.IsBackground = true;
            readerThread.Name = "QuickWindowCapture elevated input bridge";
            readerThread.Start();
            NotifyState(false, "관리자 입력 도우미 연결 중");
            return true;
        }

        internal void UpdateSettings(AppSettings updatedSettings)
        {
            lock (settingsLock)
            {
                settings = updatedSettings.Copy();
            }

            if (connected)
            {
                if (!TrySendConfiguration())
                {
                    NotifyError("관리자 입력 도우미에 변경된 단축키를 전달하지 못했습니다.");
                }
            }
        }

        private void RunConnection()
        {
            bool failureReported = false;
            try
            {
                PipeConnectionGuard.Wait(
                    pipe,
                    helperProcess,
                    ConnectionTimeoutMilliseconds,
                    delegate { return disposed; });
                if (disposed)
                {
                    return;
                }

                uint actualProcessId;
                if (!NativeMethods.GetNamedPipeClientProcessId(
                    pipe.SafePipeHandle.DangerousGetHandle(),
                    out actualProcessId) ||
                    helperProcess == null ||
                    actualProcessId != (uint)helperProcess.Id)
                {
                    throw new InvalidOperationException("입력 도우미의 프로세스 신원을 확인하지 못했습니다.");
                }

                using (StreamReader reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                {
                    writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
                    writer.AutoFlush = true;

                    string hello = reader.ReadLine();
                    if (!string.Equals(hello, InputPipeProtocol.HelloMessage, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("입력 도우미의 연결 인사가 올바르지 않습니다.");
                    }

                    connected = true;
                    if (!TrySendConfiguration())
                    {
                        throw new IOException("입력 도우미에 초기 단축키를 전달하지 못했습니다.");
                    }
                    NotifyState(true, "관리자 입력 도우미 연결됨");

                    string message;
                    while (!disposed && (message = reader.ReadLine()) != null)
                    {
                        ProcessMessage(message);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException exception)
            {
                if (!disposed)
                {
                    failureReported = true;
                    NotifyError("관리자 입력 도우미 연결이 끊어졌습니다. " + exception.Message);
                }
            }
            catch (Exception exception)
            {
                if (!disposed)
                {
                    failureReported = true;
                    NotifyError(exception.Message);
                }
            }
            finally
            {
                connected = false;
                if (!disposed)
                {
                    NotifyState(false, "관리자 입력 도우미 연결 끊김");
                    if (!failureReported)
                    {
                        NotifyError("관리자 입력 도우미 연결이 종료되었습니다.");
                    }
                }
            }
        }

        private void ProcessMessage(string message)
        {
            IntPtr windowHandle;
            if (InputPipeProtocol.TryParseCapture(message, out windowHandle))
            {
                if (NativeMethods.IsWindow(windowHandle) && captureRequested != null)
                {
                    captureRequested(windowHandle);
                }
                return;
            }

            string errorMessage;
            if (InputPipeProtocol.TryParseError(message, out errorMessage))
            {
                NotifyError("관리자 입력 도우미: " + errorMessage);
            }
        }

        private bool TrySendConfiguration()
        {
            AppSettings currentSettings;
            lock (settingsLock)
            {
                currentSettings = settings.Copy();
            }

            return TrySendLine(InputPipeProtocol.CreateConfiguration(currentSettings));
        }

        private bool TrySendLine(string message)
        {
            lock (writerLock)
            {
                if (disposed || writer == null)
                {
                    return false;
                }

                try
                {
                    writer.WriteLine(message);
                    return true;
                }
                catch (IOException)
                {
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }

        private static PipeSecurity CreatePipeSecurity()
        {
            WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier user = identity.User;
            if (user == null)
            {
                identity.Dispose();
                throw new InvalidOperationException("현재 Windows 사용자 SID를 확인하지 못했습니다.");
            }

            try
            {
                PipeSecurity security = new PipeSecurity();
                security.SetOwner(user);
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new PipeAccessRule(
                    user,
                    PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
                    AccessControlType.Allow));
                return security;
            }
            finally
            {
                identity.Dispose();
            }
        }

        private void NotifyState(bool isConnected, string message)
        {
            Action<bool, string> handler = stateChanged;
            if (handler != null)
            {
                handler(isConnected, message);
            }
        }

        private void NotifyError(string message)
        {
            Action<string> handler = errorReported;
            if (handler != null)
            {
                handler(message);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            TrySendLine(InputPipeProtocol.ExitMessage);

            disposed = true;
            connected = false;
            try
            {
                if (pipe != null)
                {
                    pipe.Dispose();
                }
            }
            catch (IOException)
            {
            }

            if (helperProcess != null)
            {
                helperProcess.Dispose();
                helperProcess = null;
            }
        }
    }
}
