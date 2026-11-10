using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SellerDesk
{
    /// <summary>
    /// A picture of the primary screen, held only in memory, so the takeover window can look
    /// transparent for the half second its shape takes to cover the desktop.
    /// </summary>
    public static class ScreenGrab
    {
        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        public static BitmapSource Primary(Visual reference)
        {
            var dpi = VisualTreeHelper.GetDpi(reference);
            var w = (int)Math.Round(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX);
            var h = (int)Math.Round(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY);
            using (var bitmap = new Bitmap(w, h))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                    graphics.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(w, h));
                var handle = bitmap.GetHbitmap();
                try
                {
                    var image = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    image.Freeze();
                    return image;
                }
                finally
                {
                    DeleteObject(handle);
                }
            }
        }
    }
}
