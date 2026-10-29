using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace QuickWindowCapture
{
    internal static class ScreenshotService
    {
        private static readonly object FileNameLock = new object();

        internal static string CaptureVisibleWindow(IntPtr windowHandle, string saveFolder)
        {
            using (Bitmap bitmap = CaptureVisibleWindowBitmap(windowHandle))
            {
                return SaveBitmap(bitmap, saveFolder);
            }
        }

        internal static Bitmap CaptureVisibleWindowBitmap(IntPtr windowHandle)
        {
            Rectangle visibleRectangle = GetVisibleWindowBounds(windowHandle);
            Bitmap bitmap = new Bitmap(visibleRectangle.Width, visibleRectangle.Height, PixelFormat.Format32bppArgb);

            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(
                        visibleRectangle.Location,
                        Point.Empty,
                        visibleRectangle.Size,
                        CopyPixelOperation.SourceCopy);
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        internal static Rectangle GetVisibleWindowBounds(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero || !NativeMethods.IsWindow(windowHandle))
            {
                throw new InvalidOperationException("캡처할 활성 창을 찾지 못했습니다.");
            }

            NativeMethods.Rect nativeRectangle;
            int result = NativeMethods.DwmGetWindowAttribute(
                windowHandle,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out nativeRectangle,
                Marshal.SizeOf(typeof(NativeMethods.Rect)));

            if (result != 0 && !NativeMethods.GetWindowRect(windowHandle, out nativeRectangle))
            {
                throw new InvalidOperationException("활성 창의 크기를 확인하지 못했습니다.");
            }

            Rectangle windowRectangle = Rectangle.FromLTRB(
                nativeRectangle.Left,
                nativeRectangle.Top,
                nativeRectangle.Right,
                nativeRectangle.Bottom);
            Rectangle visibleRectangle = Rectangle.Intersect(windowRectangle, SystemInformation.VirtualScreen);

            if (visibleRectangle.Width <= 0 || visibleRectangle.Height <= 0)
            {
                throw new InvalidOperationException("활성 창이 화면에 보이지 않습니다.");
            }

            return visibleRectangle;
        }

        internal static string SaveBitmap(Bitmap bitmap, string saveFolder)
        {
            Directory.CreateDirectory(saveFolder);
            string outputPath;
            lock (FileNameLock)
            {
                outputPath = CreateAvailablePath(saveFolder);
                bitmap.Save(outputPath, ImageFormat.Png);
            }
            return outputPath;
        }

        private static string CreateAvailablePath(string folder)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
            string candidate = Path.Combine(folder, "Capture_" + timestamp + ".png");
            int suffix = 2;

            while (File.Exists(candidate))
            {
                candidate = Path.Combine(folder, "Capture_" + timestamp + "_" + suffix + ".png");
                suffix++;
            }

            return candidate;
        }
    }
}
