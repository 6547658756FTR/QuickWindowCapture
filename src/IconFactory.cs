using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace QuickWindowCapture
{
    internal sealed class IconResource : IDisposable
    {
        private IntPtr handle;

        internal Icon Icon { get; private set; }

        private IconResource(IntPtr iconHandle)
        {
            handle = iconHandle;
            Icon = Icon.FromHandle(handle);
        }

        internal static IconResource Create()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);

                using (SolidBrush background = new SolidBrush(Color.FromArgb(37, 99, 235)))
                {
                    graphics.FillEllipse(background, 1, 1, 30, 30);
                }

                using (Pen windowPen = new Pen(Color.White, 2.2f))
                {
                    windowPen.LineJoin = LineJoin.Round;
                    graphics.DrawRectangle(windowPen, 7, 8, 18, 14);
                    graphics.DrawLine(windowPen, 8, 12, 24, 12);
                }

                using (SolidBrush shutter = new SolidBrush(Color.FromArgb(250, 204, 21)))
                using (Pen shutterOutline = new Pen(Color.White, 1.4f))
                {
                    graphics.FillEllipse(shutter, 12, 14, 8, 8);
                    graphics.DrawEllipse(shutterOutline, 12, 14, 8, 8);
                }

                return new IconResource(bitmap.GetHicon());
            }
        }

        public void Dispose()
        {
            if (Icon != null)
            {
                Icon.Dispose();
                Icon = null;
            }

            if (handle != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(handle);
                handle = IntPtr.Zero;
            }
        }
    }
}
