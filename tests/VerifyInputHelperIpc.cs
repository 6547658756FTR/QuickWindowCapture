using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using QuickWindowCapture;

internal static class VerifyInputHelperIpc
{
    private static int Main()
    {
        try
        {
            return RunTest();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("IPC 테스트 예외 형식: " + exception.GetType().FullName);
            Console.Error.WriteLine("IPC 테스트 예외 메시지: " + exception.Message);
            if (exception.InnerException != null)
            {
                Console.Error.WriteLine("내부 예외 형식: " + exception.InnerException.GetType().FullName);
                Console.Error.WriteLine("내부 예외 메시지: " + exception.InnerException.Message);
            }
            return 1;
        }
    }

    private static int RunTest()
    {
        NativeMethods.RequireSystemDllSearchOnly();
        string pipeName = "QuickWindowCapture.Input." + Guid.NewGuid().ToString("N");
        Exception helperError = null;

        using (NamedPipeServerStream server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.None))
        {
            Thread helperThread = new Thread(new ThreadStart(delegate
            {
                try
                {
                    using (ElevatedInputHelperContext context = new ElevatedInputHelperContext(
                        pipeName,
                        Process.GetCurrentProcess().Id))
                    {
                        Application.Run(context);
                    }
                }
                catch (Exception exception)
                {
                    helperError = exception;
                }
            }));
            helperThread.IsBackground = true;
            helperThread.SetApartmentState(ApartmentState.STA);
            helperThread.Start();

            server.WaitForConnection();
            uint clientProcessId;
            Require(
                NativeMethods.GetNamedPipeClientProcessId(
                    server.SafePipeHandle.DangerousGetHandle(),
                    out clientProcessId),
                "클라이언트 프로세스 ID를 확인하지 못했습니다.");
            Require(clientProcessId == (uint)Process.GetCurrentProcess().Id,
                "예상하지 않은 프로세스가 입력 채널에 연결되었습니다.");

            using (StreamReader reader = new StreamReader(
                server,
                new UTF8Encoding(false),
                false,
                4096,
                true))
            using (StreamWriter writer = new StreamWriter(
                server,
                new UTF8Encoding(false),
                4096,
                true))
            {
                writer.AutoFlush = true;
                Require(reader.ReadLine() == InputPipeProtocol.HelloMessage,
                    "입력 도우미 연결 인사가 올바르지 않습니다.");

                AppSettings settings = AppSettings.CreateDefault();
                settings.CaptureKey = Keys.F12;
                settings.Modifiers = HotkeyModifiers.Control;
                writer.WriteLine(InputPipeProtocol.CreateConfiguration(settings));
                Thread.Sleep(100);
                writer.WriteLine(InputPipeProtocol.ExitMessage);
            }

            Require(helperThread.Join(3000), "입력 도우미가 종료 명령에 응답하지 않았습니다.");
        }

        if (helperError != null)
        {
            throw new InvalidOperationException("입력 도우미 IPC 테스트 실패", helperError);
        }

        Console.WriteLine("관리자 입력 도우미 IPC 연결 및 종료 테스트 통과");
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
