using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace QuickWindowCapture
{
    internal static class ElevatedInputHelper
    {
        private const string HelperArgument = "--input-helper";
        private const string PipeArgument = "--pipe";
        private const string ParentArgument = "--parent";

        internal static bool IsHelperInvocation(string[] args)
        {
            return FindArgument(args, HelperArgument) >= 0;
        }

        internal static void Run(string[] args)
        {
            if (!PrivilegeManager.IsAdministrator())
            {
                return;
            }

            string pipeName;
            int parentProcessId;
            if (!TryReadArguments(args, out pipeName, out parentProcessId) ||
                !VerifyParent(parentProcessId))
            {
                return;
            }

            try
            {
                using (ElevatedInputHelperContext context = new ElevatedInputHelperContext(
                    pipeName,
                    parentProcessId))
                {
                    Application.Run(context);
                }
            }
            catch
            {
                // The helper intentionally has no file or UI output. The normal
                // process detects a failed pipe connection and reports it there.
            }
        }

        private static bool TryReadArguments(string[] args, out string pipeName, out int parentProcessId)
        {
            pipeName = null;
            parentProcessId = 0;
            int pipeIndex = FindArgument(args, PipeArgument);
            int parentIndex = FindArgument(args, ParentArgument);
            if (pipeIndex < 0 || pipeIndex + 1 >= args.Length ||
                parentIndex < 0 || parentIndex + 1 >= args.Length)
            {
                return false;
            }

            string value = args[pipeIndex + 1];
            const string prefix = "QuickWindowCapture.Input.";
            Guid pipeId;
            if (!value.StartsWith(prefix, StringComparison.Ordinal) ||
                !Guid.TryParseExact(value.Substring(prefix.Length), "N", out pipeId) ||
                !int.TryParse(
                    args[parentIndex + 1],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out parentProcessId) ||
                parentProcessId <= 0 ||
                parentProcessId == Process.GetCurrentProcess().Id)
            {
                return false;
            }

            pipeName = value;
            return true;
        }

        private static bool VerifyParent(int parentProcessId)
        {
            try
            {
                using (Process parent = Process.GetProcessById(parentProcessId))
                using (Process current = Process.GetCurrentProcess())
                {
                    if (parent.SessionId != current.SessionId)
                    {
                        return false;
                    }

                    string parentPath = Path.GetFullPath(parent.MainModule.FileName);
                    string currentPath = Path.GetFullPath(Application.ExecutablePath);
                    return string.Equals(parentPath, currentPath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private static int FindArgument(string[] args, string expected)
        {
            for (int index = 0; index < args.Length; index++)
            {
                if (string.Equals(args[index], expected, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }
    }

    internal sealed class ElevatedInputHelperContext : ApplicationContext
    {
        private readonly NamedPipeClientStream pipe;
        private readonly StreamReader reader;
        private readonly StreamWriter writer;
        private readonly Control dispatcher;
        private readonly object writerLock = new object();
        private readonly object queueLock = new object();
        private readonly Queue<IntPtr> pendingSignals = new Queue<IntPtr>();
        private readonly AutoResetEvent signalAvailable = new AutoResetEvent(false);
        private readonly Thread writerThread;
        private readonly Thread readerThread;
        private KeyboardHook keyboardHook;
        private RawInputMonitor rawInputMonitor;
        private volatile bool exiting;

        internal ElevatedInputHelperContext(string pipeName, int expectedParentProcessId)
        {
            pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(10000);

            uint serverProcessId;
            if (!NativeMethods.GetNamedPipeServerProcessId(
                pipe.SafePipeHandle.DangerousGetHandle(),
                out serverProcessId) ||
                serverProcessId != (uint)expectedParentProcessId)
            {
                throw new InvalidOperationException("입력 채널의 프로세스 신원을 확인하지 못했습니다.");
            }

            reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true);
            writer.AutoFlush = true;
            writer.WriteLine(InputPipeProtocol.HelloMessage);

            string firstMessage = reader.ReadLine();
            AppSettings initialSettings;
            if (!InputPipeProtocol.TryParseConfiguration(firstMessage, out initialSettings))
            {
                throw new InvalidOperationException("초기 입력 설정이 올바르지 않습니다.");
            }

            dispatcher = new Control();
            dispatcher.CreateControl();

            writerThread = new Thread(WriteSignals);
            writerThread.IsBackground = true;
            writerThread.Name = "QuickWindowCapture elevated input writer";
            writerThread.Start();

            StartInputMonitors(initialSettings);

            readerThread = new Thread(ReadCommands);
            readerThread.IsBackground = true;
            readerThread.Name = "QuickWindowCapture elevated input reader";
            readerThread.Start();
        }

        private void StartInputMonitors(AppSettings settings)
        {
            Exception hookError = null;
            Exception rawInputError = null;
            try
            {
                keyboardHook = new KeyboardHook(settings);
                keyboardHook.CaptureRequested += QueueCapture;
                keyboardHook.Start();
            }
            catch (Exception exception)
            {
                hookError = exception;
                if (keyboardHook != null)
                {
                    keyboardHook.Dispose();
                    keyboardHook = null;
                }
            }

            try
            {
                rawInputMonitor = new RawInputMonitor(settings);
                rawInputMonitor.CaptureRequested += QueueCapture;
            }
            catch (Exception exception)
            {
                rawInputError = exception;
                if (rawInputMonitor != null)
                {
                    rawInputMonitor.Dispose();
                    rawInputMonitor = null;
                }
            }

            if (hookError != null && rawInputError != null)
            {
                SendError(hookError.Message + " " + rawInputError.Message);
                throw new InvalidOperationException("관리자 입력 감지를 시작하지 못했습니다.");
            }
        }

        private void QueueCapture(IntPtr windowHandle)
        {
            lock (queueLock)
            {
                if (exiting || pendingSignals.Count >= 8)
                {
                    return;
                }

                pendingSignals.Enqueue(windowHandle);
            }
            signalAvailable.Set();
        }

        private void WriteSignals()
        {
            while (!exiting)
            {
                signalAvailable.WaitOne();
                if (exiting)
                {
                    break;
                }

                while (true)
                {
                    IntPtr windowHandle;
                    lock (queueLock)
                    {
                        if (pendingSignals.Count == 0)
                        {
                            break;
                        }
                        windowHandle = pendingSignals.Dequeue();
                    }

                    try
                    {
                        SendLine(InputPipeProtocol.CreateCapture(windowHandle));
                    }
                    catch
                    {
                        PostExit();
                        return;
                    }
                }
            }
        }

        private void ReadCommands()
        {
            try
            {
                string message;
                while (!exiting && (message = reader.ReadLine()) != null)
                {
                    if (string.Equals(message, InputPipeProtocol.ExitMessage, StringComparison.Ordinal))
                    {
                        break;
                    }

                    AppSettings updatedSettings;
                    if (InputPipeProtocol.TryParseConfiguration(message, out updatedSettings))
                    {
                        PostSettings(updatedSettings);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                PostExit();
            }
        }

        private void PostSettings(AppSettings settings)
        {
            try
            {
                dispatcher.BeginInvoke((MethodInvoker)delegate
                {
                    if (keyboardHook != null)
                    {
                        keyboardHook.UpdateSettings(settings);
                    }
                    if (rawInputMonitor != null)
                    {
                        rawInputMonitor.UpdateSettings(settings);
                    }
                });
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void SendError(string message)
        {
            try
            {
                SendLine(InputPipeProtocol.CreateError(message));
            }
            catch
            {
            }
        }

        private void SendLine(string message)
        {
            lock (writerLock)
            {
                if (!exiting)
                {
                    writer.WriteLine(message);
                }
            }
        }

        private void PostExit()
        {
            try
            {
                dispatcher.BeginInvoke((MethodInvoker)ExitHelper);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void ExitHelper()
        {
            if (exiting)
            {
                return;
            }

            exiting = true;
            signalAvailable.Set();
            if (keyboardHook != null)
            {
                keyboardHook.Dispose();
                keyboardHook = null;
            }
            if (rawInputMonitor != null)
            {
                rawInputMonitor.Dispose();
                rawInputMonitor = null;
            }
            try
            {
                pipe.Dispose();
            }
            catch (IOException)
            {
            }
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ExitHelper();
                JoinBackgroundThread(writerThread);
                JoinBackgroundThread(readerThread);
                signalAvailable.Dispose();
                try
                {
                    reader.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
                try
                {
                    writer.Dispose();
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                dispatcher.Dispose();
            }
            base.Dispose(disposing);
        }

        private static void JoinBackgroundThread(Thread thread)
        {
            if (thread != null && thread != Thread.CurrentThread && thread.IsAlive)
            {
                thread.Join(1000);
            }
        }
    }
}
