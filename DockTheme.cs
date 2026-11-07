using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Islandock;

/// <summary>
/// ライト / ダークモードごとの色。Windows の設定に合わせて自動で切り替わる。
/// </summary>
public sealed class DockTheme
{
    public bool IsDark { get; private init; }

    public Brush BackgroundTint { get; private init; } = Brushes.Transparent;
    public Brush OuterBorder { get; private init; } = Brushes.Transparent;
    public Brush InnerHighlight { get; private init; } = Brushes.Transparent;
    public Brush Separator { get; private init; } = Brushes.Transparent;
    public Brush Dot { get; private init; } = Brushes.Transparent;
    public Brush LabelBackground { get; private init; } = Brushes.Transparent;
    public Brush LabelBorder { get; private init; } = Brushes.Transparent;
    public Brush LabelForeground { get; private init; } = Brushes.Transparent;
    public Brush TileFill { get; private init; } = Brushes.Transparent;
    public Brush TileStroke { get; private init; } = Brushes.Transparent;

    /// <summary>すりガラスに重ねる色（AABBGGRR 形式）</summary>
    public uint AcrylicColor { get; private init; }

    public static DockTheme Current() => IsSystemDark() ? Dark : Light;

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    private static SolidColorBrush B(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Grad(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
    }

    public static readonly DockTheme Light = new()
    {
        IsDark = false,
        BackgroundTint = B(0x50, 0xF4, 0xF4, 0xF6), // 約31%の不透明度（かなり透ける）
        OuterBorder = B(0x26, 0x00, 0x00, 0x00),
        InnerHighlight = B(0x80, 0xFF, 0xFF, 0xFF),
        Separator = B(0x30, 0x00, 0x00, 0x00),
        Dot = B(0x99, 0x00, 0x00, 0x00),
        LabelBackground = B(0xEB, 0xF2, 0xF2, 0xF2),
        LabelBorder = B(0x1F, 0x00, 0x00, 0x00),
        LabelForeground = B(0xFF, 0x1D, 0x1D, 0x1F),
        TileFill = Grad(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xEC, 0xEC, 0xF0)),
        TileStroke = B(0x1A, 0x00, 0x00, 0x00),
        AcrylicColor = 0x80F4F4F4,
    };

    public static readonly DockTheme Dark = new()
    {
        IsDark = true,
        BackgroundTint = B(0x50, 0x26, 0x26, 0x28), // 約31%の不透明度（かなり透ける）
        OuterBorder = B(0x80, 0x00, 0x00, 0x00),
        InnerHighlight = B(0x26, 0xFF, 0xFF, 0xFF),
        Separator = B(0x40, 0xFF, 0xFF, 0xFF),
        Dot = B(0xB3, 0xFF, 0xFF, 0xFF),
        LabelBackground = B(0xEB, 0x2C, 0x2C, 0x2E),
        LabelBorder = B(0x33, 0xFF, 0xFF, 0xFF),
        LabelForeground = B(0xFF, 0xF5, 0xF5, 0xF7),
        TileFill = Grad(Color.FromRgb(0x48, 0x48, 0x4C), Color.FromRgb(0x30, 0x30, 0x34)),
        TileStroke = B(0x26, 0xFF, 0xFF, 0xFF),
        AcrylicColor = 0x80262626,
    };

    /// <summary>右クリックメニューの色をアプリ全体のリソースに反映する</summary>
    public void ApplyMenuResources()
    {
        var res = Application.Current.Resources;
        if (IsDark)
        {
            res["MenuBg"] = B(0xF2, 0x2A, 0x2A, 0x2C);
            res["MenuBorder"] = B(0x40, 0xFF, 0xFF, 0xFF);
            res["MenuFg"] = B(0xFF, 0xF2, 0xF2, 0xF2);
            res["MenuDisabledFg"] = B(0x70, 0xFF, 0xFF, 0xFF);
            res["MenuSeparator"] = B(0x30, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            res["MenuBg"] = B(0xF5, 0xF6, 0xF6, 0xF6);
            res["MenuBorder"] = B(0x26, 0x00, 0x00, 0x00);
            res["MenuFg"] = B(0xFF, 0x1D, 0x1D, 0x1F);
            res["MenuDisabledFg"] = B(0x60, 0x00, 0x00, 0x00);
            res["MenuSeparator"] = B(0x1F, 0x00, 0x00, 0x00);
        }
        res["MenuHover"] = B(0xFF, 0x2D, 0x7C, 0xF6); // macOS 風のアクセントブルー
    }
}
