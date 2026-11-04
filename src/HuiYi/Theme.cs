using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace HuiYi.App;

/// The same colours as App.xaml, for code-built views.
public static class Theme
{
    public static readonly Brush Ink = Frozen(0x1E, 0x22, 0x35);
    public static readonly Brush Primary = Frozen(0x4F, 0x5B, 0xD5);
    public static readonly Brush PrimarySoft = Frozen(0xEC, 0xEE, 0xFC);
    public static readonly Brush OnAir = Frozen(0x22, 0xB0, 0x7D);
    public static readonly Brush Warn = Frozen(0xD9, 0x82, 0x2B);
    public static readonly Brush Grey = Frozen(0x64, 0x6B, 0x80);
    public static readonly Brush Grey2 = Frozen(0x9A, 0xA1, 0xB2);
    public static readonly Brush Line = Frozen(0xE3, 0xE6, 0xEE);
    public static readonly Brush Surface = Frozen(0xFF, 0xFF, 0xFF);
    public static readonly Brush NightText = Frozen(0xE8, 0xEA, 0xF2);
    public static readonly Brush NightMuted = Frozen(0x8E, 0x94, 0xA8);
    public static readonly Brush NightRaised = Frozen(0x25, 0x29, 0x39);

    /// 发言人 1, 2…: told apart at a glance, readable on the dark panel and on white.
    private static readonly Brush[] Speakers =
    [
        Frozen(0x5B, 0x8D, 0xEF), Frozen(0x2F, 0xB3, 0x8A), Frozen(0xB0, 0x7C, 0xE8),
        Frozen(0xE0, 0x9B, 0x3D), Frozen(0x3B, 0xB4, 0xC9), Frozen(0xE0, 0x6C, 0x9A),
    ];

    public static Brush Speaker(int number) => Speakers[(number - 1) % Speakers.Length];

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var x = new SolidColorBrush(Color.FromRgb(r, g, b));
        x.Freeze();
        return x;
    }
}
