using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;

namespace QuickWindowCapture
{
    internal static class PipeConnectionGuard
    {
        private const int PollIntervalMilliseconds = 50;

        internal static void Wait(
            NamedPipeServerStream pipe,
            Process helperProcess,
            int timeoutMilliseconds,
            Func<bool> cancellationRequested)
        {
            if (pipe == null)
            {
                throw new ArgumentNullException("pipe");
            }
            if (helperProcess == null)
            {
                throw new ArgumentNullException("helperProcess");
            }
            if (timeoutMilliseconds <= 0)
            {
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            }

            IAsyncResult pendingConnection = pipe.BeginWaitForConnection(null, null);
            WaitHandle connectionCompleted = pendingConnection.AsyncWaitHandle;
            Stopwatch timer = Stopwatch.StartNew();
            bool endCalled = false;
            try
            {
                while (!connectionCompleted.WaitOne(PollIntervalMilliseconds))
                {
                    if (cancellationRequested != null && cancellationRequested())
                    {
                        CancelPendingConnection(pipe, pendingConnection);
                        endCalled = true;
                        throw new ObjectDisposedException("pipe");
                    }

                    if (HasExited(helperProcess))
                    {
                        CancelPendingConnection(pipe, pendingConnection);
                        endCalled = true;
                        throw new InvalidOperationException(
                            "관리자 입력 도우미가 연결하기 전에 종료되었습니다.");
                    }

                    if (timer.ElapsedMilliseconds >= timeoutMilliseconds)
                    {
                        CancelPendingConnection(pipe, pendingConnection);
                        endCalled = true;
                        throw new TimeoutException(
                            "관리자 입력 도우미가 " +
                            (timeoutMilliseconds / 1000).ToString() +
                            "초 안에 연결되지 않았습니다.");
                    }
                }

                pipe.EndWaitForConnection(pendingConnection);
                endCalled = true;
            }
            finally
            {
                if (!endCalled && pendingConnection.IsCompleted)
                {
                    try
                    {
                        pipe.EndWaitForConnection(pendingConnection);
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
                connectionCompleted.Close();
            }
        }

        private static bool HasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The connection timeout still protects this path when Windows
                // temporarily refuses a process-state query.
                return false;
            }
        }

        private static void CancelPendingConnection(
            NamedPipeServerStream pipe,
            IAsyncResult pendingConnection)
        {
            try
            {
                pipe.Dispose();
            }
            catch (IOException)
            {
            }

            try
            {
                pipe.EndWaitForConnection(pendingConnection);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
