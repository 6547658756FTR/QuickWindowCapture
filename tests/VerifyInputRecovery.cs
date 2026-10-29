using System;
using System.Diagnostics;
using QuickWindowCapture;

internal static class VerifyInputRecovery
{
    private static int Main()
    {
        long step = Stopwatch.Frequency / 1000;
        CaptureKeyLatch latch = new CaptureKeyLatch();

        Require(latch.TryPress(100 * step), "첫 입력이 허용되지 않았습니다.");
        Require(!latch.TryPress(200 * step), "짧은 반복 입력이 허용되었습니다.");
        Require(!latch.TryPress(500 * step), "키 반복 중 잠금이 풀렸습니다.");
        Require(latch.TryPress(1300 * step), "해제 신호 누락 후 자동 복구되지 않았습니다.");
        latch.Release(1350 * step);
        Require(latch.TryPress(1400 * step), "정상 KeyUp 뒤 다음 입력이 허용되지 않았습니다.");
        latch.Reset();
        Require(latch.TryPress(1401 * step), "초기화 뒤 입력이 허용되지 않았습니다.");

        Console.WriteLine("입력 잠금 자동 복구 테스트 통과");
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
