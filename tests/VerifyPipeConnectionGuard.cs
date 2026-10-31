using System;
using System.Diagnostics;
using System.IO.Pipes;
using QuickWindowCapture;

internal static class VerifyPipeConnectionGuard
{
    private static int Main()
    {
        string pipeName = "QuickWindowCapture.TimeoutTest." + Guid.NewGuid().ToString("N");
        Stopwatch timer = Stopwatch.StartNew();
        bool timedOut = false;

        using (NamedPipeServerStream pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous))
        using (Process currentProcess = Process.GetCurrentProcess())
        {
            try
            {
                PipeConnectionGuard.Wait(pipe, currentProcess, 250, delegate { return false; });
            }
            catch (TimeoutException)
            {
                timedOut = true;
            }
        }

        timer.Stop();
        Require(timedOut, "연결되지 않은 파이프가 시간 제한으로 중단되지 않았습니다.");
        Require(timer.ElapsedMilliseconds >= 200, "연결 제한이 지나치게 일찍 발생했습니다.");
        Require(timer.ElapsedMilliseconds < 3000, "연결 제한 처리가 지연되었습니다.");

        string exitPipeName = "QuickWindowCapture.ExitTest." + Guid.NewGuid().ToString("N");
        Stopwatch exitTimer = Stopwatch.StartNew();
        bool processExitDetected = false;
        using (Process exitedProcess = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true
        }))
        {
            exitedProcess.WaitForExit(2000);
            using (NamedPipeServerStream pipe = new NamedPipeServerStream(
                exitPipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous))
            {
                try
                {
                    PipeConnectionGuard.Wait(pipe, exitedProcess, 5000, delegate { return false; });
                }
                catch (InvalidOperationException)
                {
                    processExitDetected = true;
                }
            }
        }
        exitTimer.Stop();
        Require(processExitDetected, "도우미 프로세스의 조기 종료를 감지하지 못했습니다.");
        Require(exitTimer.ElapsedMilliseconds < 1500, "도우미 종료 감지가 지연되었습니다.");

        Console.WriteLine(
            "입력 도우미 연결 제한 테스트 통과: 시간 제한 " +
            timer.ElapsedMilliseconds + "ms, 프로세스 종료 감지 " +
            exitTimer.ElapsedMilliseconds + "ms");
        return 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
