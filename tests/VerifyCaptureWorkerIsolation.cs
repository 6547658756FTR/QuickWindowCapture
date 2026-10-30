using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using QuickWindowCapture;

internal static class VerifyCaptureWorkerIsolation
{
    private static int Main()
    {
        ManualResetEvent operationStarted = new ManualResetEvent(false);
        ManualResetEvent releaseOperation = new ManualResetEvent(false);
        int deliveredResults = 0;

        AppSettings settings = AppSettings.CreateDefault();
        settings.CaptureMode = CaptureMode.Compatibility;

        CaptureWorker worker = new CaptureWorker(
            7,
            settings,
            false,
            delegate(int generation, Bitmap bitmap, AppSettings requestSettings, long elapsed)
            {
                Interlocked.Increment(ref deliveredResults);
                bitmap.Dispose();
            },
            delegate { },
            delegate
            {
                operationStarted.Set();
                releaseOperation.WaitOne();
                return new Bitmap(1, 1);
            });

        Stopwatch enqueueTimer = Stopwatch.StartNew();
        Require(worker.TryEnqueue(new IntPtr(1), settings), "첫 캡처 요청이 거부되었습니다.");
        enqueueTimer.Stop();
        Require(enqueueTimer.ElapsedMilliseconds < 100, "입력 스레드가 캡처 작업을 기다렸습니다.");
        Require(operationStarted.WaitOne(1000), "가상 정지 작업이 시작되지 않았습니다.");

        Require(worker.TryEnqueue(new IntPtr(2), settings), "첫 대기 요청이 거부되었습니다.");
        Require(worker.TryEnqueue(new IntPtr(3), settings), "두 번째 대기 요청이 거부되었습니다.");
        Require(!worker.TryEnqueue(new IntPtr(4), settings), "대기열 상한이 적용되지 않았습니다.");

        Thread.Sleep(80);
        Require(worker.HasTimedOut(50), "정지한 캡처 작업을 시간 초과로 감지하지 못했습니다.");

        worker.Retire();
        releaseOperation.Set();
        Thread.Sleep(80);
        Require(deliveredResults == 0, "격리된 작업자의 늦은 결과가 전달되었습니다.");

        worker.Dispose();
        operationStarted.Dispose();
        releaseOperation.Dispose();
        Console.WriteLine("캡처 작업자 격리 및 시간 제한 테스트 통과");
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
