using System;
using System.Windows.Forms;
using QuickWindowCapture;

internal static class VerifyInputPipeProtocol
{
    private static int Main()
    {
        AppSettings settings = AppSettings.CreateDefault();
        settings.CaptureKey = Keys.F9;
        settings.Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift;

        AppSettings parsedSettings;
        Require(
            InputPipeProtocol.TryParseConfiguration(
                InputPipeProtocol.CreateConfiguration(settings),
                out parsedSettings),
            "입력 설정 왕복 변환에 실패했습니다.");
        Require(parsedSettings.CaptureKey == Keys.F9, "캡처 키가 달라졌습니다.");
        Require(parsedSettings.Modifiers == settings.Modifiers, "보조 키가 달라졌습니다.");
        Require(!InputPipeProtocol.TryParseConfiguration("CONFIG|0|0", out parsedSettings),
            "유효하지 않은 키가 허용되었습니다.");
        Require(!InputPipeProtocol.TryParseConfiguration("CONFIG|44|64", out parsedSettings),
            "허용하지 않은 보조 키 비트가 허용되었습니다.");

        IntPtr parsedWindow;
        IntPtr originalWindow = new IntPtr(123456);
        Require(
            InputPipeProtocol.TryParseCapture(
                InputPipeProtocol.CreateCapture(originalWindow),
                out parsedWindow),
            "창 핸들 왕복 변환에 실패했습니다.");
        Require(parsedWindow == originalWindow, "창 핸들이 달라졌습니다.");
        Require(!InputPipeProtocol.TryParseCapture("CAPTURE|0", out parsedWindow),
            "빈 창 핸들이 허용되었습니다.");

        string parsedError;
        Require(
            InputPipeProtocol.TryParseError(
                InputPipeProtocol.CreateError("테스트 오류|줄바꿈\r\n포함"),
                out parsedError),
            "오류 메시지 왕복 변환에 실패했습니다.");
        Require(parsedError == "테스트 오류|줄바꿈\r\n포함", "오류 메시지가 달라졌습니다.");
        Require(!InputPipeProtocol.TryParseError("ERROR|not-base64!", out parsedError),
            "손상된 오류 메시지가 허용되었습니다.");

        Console.WriteLine("관리자 입력 IPC 프로토콜 검증 테스트 통과");
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
