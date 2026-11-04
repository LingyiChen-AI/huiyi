using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HuiYi.App;

/// 会译's mark, drawn in code: a round white AI buddy with a glowing spark antenna, winking over a
/// meeting note card, on mint. While listening a red dot sits in the corner. Drawn on a 100×100
/// grid (the same as assets/icon.svg) and scaled.
public static class Icons
{
    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Brush Solid(string hex, double opacity = 1)
    {
        var b = new SolidColorBrush(Hex(hex)) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    private static Geometry Path(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    /// A four-pointed spark centred on (x, y).
    private static Geometry Spark(double x, double y, double r)
    {
        var k = r * 0.18;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string P(double a, double b) => string.Format(inv, "{0:0.###},{1:0.###}", a, b);
        return Path($"M{P(x, y - r)} C{P(x + k, y - k)} {P(x + k, y - k)} {P(x + r, y)} " +
                    $"C{P(x + k, y + k)} {P(x + k, y + k)} {P(x, y + r)} " +
                    $"C{P(x - k, y + k)} {P(x - k, y + k)} {P(x - r, y)} " +
                    $"C{P(x - k, y - k)} {P(x - k, y - k)} {P(x, y - r)} Z");
    }

    public static BitmapSource Mark(int size, bool live)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 100.0, size / 100.0));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, 100, 100), 23, 23));

            var bg = new LinearGradientBrush(Hex("#34D6A8"), Hex("#119C7A"), new Point(0, 0), new Point(1, 1));
            dc.DrawRectangle(bg, null, new Rect(0, 0, 100, 100));
            var sheen = new LinearGradientBrush(Color.FromArgb(56, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            dc.DrawRectangle(sheen, null, new Rect(0, 0, 100, 50));

            // Antenna with the AI spark.
            dc.DrawGeometry(null, new Pen(Brushes.White, 3.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Path("M50,30 Q46,22 50,14"));
            dc.DrawGeometry(Solid("#FFE066"), null, Spark(51, 11, 8));

            // The buddy: one open eye, one wink, blush, a small smile.
            dc.DrawEllipse(Brushes.White, null, new Point(50, 64), 38, 34);
            var ink = Solid("#15171C");
            dc.DrawEllipse(ink, null, new Point(38, 54), 4.6, 6);
            dc.DrawEllipse(Brushes.White, null, new Point(39.4, 51.8), 1.6, 1.6);
            var line = new Pen(ink, 3.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawGeometry(null, line, Path("M57,54 Q62,49 67,54"));
            var blush = Solid("#FF9EB8", 0.75);
            dc.DrawEllipse(blush, null, new Point(30, 62), 4.2, 4.2);
            dc.DrawEllipse(blush, null, new Point(72, 62), 4.2, 4.2);
            dc.DrawGeometry(null, new Pen(ink, 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Path("M46,60 Q50,64 54,60"));

            // The note card it leans on: what was said, and its translation.
            dc.PushTransform(new RotateTransform(-3, 50, 74));
            dc.DrawRoundedRectangle(Solid("#0B5E49", 0.25), null, new Rect(-6, 62.5, 104, 52), 12, 12);
            var card = new LinearGradientBrush(Colors.White, Hex("#EEF0F7"), 90);
            dc.DrawRoundedRectangle(card, null, new Rect(-6, 64, 104, 52), 12, 12);
            dc.DrawLine(new Pen(Solid("#1E2235"), 5.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(28, 77), new Point(76, 77));
            dc.DrawLine(new Pen(Solid("#119C7A"), 5.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, new Point(28, 88), new Point(58, 88));
            dc.Pop();

            if (live)
            {
                // On air: a red dot in the top right corner, ringed so it reads on any background.
                dc.DrawEllipse(Brushes.White, null, new Point(82, 18), 15, 15);
                dc.DrawEllipse(Solid("#FF3B4E"), null, new Point(82, 18), 11, 11);
            }
            dc.Pop();
            dc.Pop();
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

    /// Writes the mark as a multi-size .ico (PNG entries, which Windows Vista+ reads) for the exe and
    /// the installer: `HuiYi.exe --export-icon assets\HuiYi.ico`.
    public static void SaveIco(string path)
    {
        int[] sizes = [16, 24, 32, 48, 64, 128, 256];
        var pngs = sizes.Select(size =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Mark(size, false)));
            using var ms = new System.IO.MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }).ToList();
        using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
        w.Write((short)0);
        w.Write((short)1); // icon
        w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(pngs[i].Length);
            w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) w.Write(png);
    }
}
