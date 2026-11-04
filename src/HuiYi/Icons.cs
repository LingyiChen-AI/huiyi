using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HuiYi.App;

/// 会译's mark, drawn in code: an indigo speech bubble holding two lines of text, the second one
/// green while listening.
public static class Icons
{
    private static readonly Color Indigo = Color.FromRgb(0x4F, 0x5B, 0xD5);
    private static readonly Color OnAir = Color.FromRgb(0x5C, 0xE0, 0xAE);

    public static BitmapSource Mark(int size, bool live)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var s = (double)size;
            var fill = new SolidColorBrush(Indigo);
            // Bubble: a round body with a tail at the lower left.
            dc.DrawRoundedRectangle(fill, null, new Rect(s * 0.04, s * 0.06, s * 0.92, s * 0.72), s * 0.26, s * 0.26);
            var tail = new StreamGeometry();
            using (var g = tail.Open())
            {
                g.BeginFigure(new Point(s * 0.2, s * 0.66), true, true);
                g.LineTo(new Point(s * 0.12, s * 0.95), true, false);
                g.LineTo(new Point(s * 0.44, s * 0.74), true, false);
            }
            dc.DrawGeometry(fill, null, tail);
            var white = new SolidColorBrush(Colors.White);
            dc.DrawRoundedRectangle(white, null, new Rect(s * 0.24, s * 0.27, s * 0.52, s * 0.1), s * 0.05, s * 0.05);
            dc.DrawRoundedRectangle(new SolidColorBrush(live ? OnAir : Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), null,
                new Rect(s * 0.24, s * 0.46, s * 0.34, s * 0.1), s * 0.05, s * 0.05);
        }
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }

    private static readonly Dictionary<bool, System.Drawing.Icon> TrayIcons = new();

    /// The tray wants a Win32 icon.
    public static System.Drawing.Icon Tray(bool live)
    {
        if (TrayIcons.TryGetValue(live, out var cached)) return cached;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Mark(32, live)));
        using var ms = new System.IO.MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        using var bitmap = new System.Drawing.Bitmap(ms);
        var icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
        TrayIcons[live] = icon;
        return icon;
    }
}
