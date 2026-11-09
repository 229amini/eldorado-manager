using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EldoradoManager
{
    /// <summary>
    /// --snapshot DIR writes each alarm state to PNG, one folder per theme, without opening a window, so
    /// the design can be reviewed and the docs images redrawn without an alarm taking over the screen.
    /// The dashboard is a page: open Web\index.html in a browser, with #dark or #light.
    /// </summary>
    public static class Snapshot
    {
        private static readonly Brush Backdrop = new LinearGradientBrush(
            Color.FromRgb(0x6B, 0x55, 0x3C), Color.FromRgb(0x2C, 0x25, 0x1E), 90);

        public static void Write(AlarmWindow window, string root)
        {
            foreach (var skin in Skin.All)
            {
                var folder = Path.Combine(root, skin.Name);
                Directory.CreateDirectory(folder);
                Skin.Use(skin.Name);
                window.ApplySkin();

                Island(window, Path.Combine(folder, "island-calm.png"), AlarmWindow.Sample(1200, 0.1));
                Island(window, Path.Combine(folder, "island-hurry.png"), AlarmWindow.Sample(1200, 0.6));
                Island(window, Path.Combine(folder, "island-notice.png"), new Alarm
                {
                    Id = "notice",
                    IsNotice = true,
                    Header = "Eldorado Manager is on watch",
                    Text = "You'll see your next order here.",
                });
                Save(window.PreviewTakeover(AlarmWindow.Sample(1200, 0.9), 62), Path.Combine(folder, "critical-left.png"));
                Save(window.PreviewTakeover(AlarmWindow.Sample(1200, 0.9), 92), Path.Combine(folder, "critical-middle.png"));
                Save(window.PreviewTakeover(AlarmWindow.Sample(1200, 0.9), 0), Path.Combine(folder, "critical-dark.png"));
                Save(window.PreviewTakeover(AlarmWindow.Sample(1200, 1.05), 115), Path.Combine(folder, "late.png"));
                Island(window, Path.Combine(folder, "island-compact.png"), AlarmWindow.Sample(1200, 0.6), true);

                var form = new SettingsWindow(new Config { Theme = skin.Name });
                var settings = (FrameworkElement)form.Content;
                foreach (var part in new[] { "general", "alarms", "key" })
                {
                    form.Open(part);
                    settings.Measure(new Size(620, double.PositiveInfinity));
                    settings.Arrange(new Rect(new Size(620, settings.DesiredSize.Height)));
                    settings.UpdateLayout();
                    Save(settings, Path.Combine(folder, "settings-" + part + ".png"), null, 48);
                }
            }
        }

        private static void Island(AlarmWindow window, string path, Alarm alarm, bool compact = false)
        {
            var island = window.PreviewIsland(alarm, compact);
            var backdrop = new DrawingVisual();
            using (var context = backdrop.RenderOpen())
                context.DrawRectangle(Backdrop, null, new Rect(0, 0, island.ActualWidth, island.ActualHeight));
            Save(island, path, backdrop);
        }

        /// <param name="margin">The element's own margin, which ActualWidth leaves out but drawing keeps.</param>
        private static void Save(FrameworkElement element, string path, Visual under = null, int margin = 0)
        {
            var image = new RenderTargetBitmap((int)element.ActualWidth + margin, (int)element.ActualHeight + margin, 96, 96, PixelFormats.Pbgra32);
            if (under != null) image.Render(under);
            image.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path)) encoder.Save(file);
        }
    }
}
