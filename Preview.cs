using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Islandock;

/// <summary>
/// 開発用: 画面に表示せずに Dock のアイコンを並べて PNG に書き出す（見た目の確認用）。
/// </summary>
internal static class Preview
{
    public static void Render(string outputPath)
    {
        var entries = DockConfig.Load();
        var settings = DockConfig.LoadSettings();
        const double size = 84;

        var rows = new StackPanel();
        foreach (var theme in new[] { DockTheme.Light, DockTheme.Dark })
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var entry in entries)
            {
                var item = new DockItem(entry, size, 6);
                item.ApplyStyle(theme, settings.IconTiles);
                item.IsRunning = true;
                panel.Children.Add(item.Visual);
            }
            var trash = new DockItem(new DockEntry { Name = "ごみ箱", Path = "::{645FF040-5081-101B-9F08-00AA002F954E}" }, size, 6, isTrash: true);
            trash.ApplyStyle(theme, settings.IconTiles);
            panel.Children.Add(trash.Visual);

            rows.Children.Add(new Border
            {
                Child = panel,
                Padding = new Thickness(16),
                Background = new LinearGradientBrush(
                    theme.IsDark ? Color.FromRgb(0x1C, 0x2B, 0x4A) : Color.FromRgb(0x8E, 0xB8, 0xE8),
                    theme.IsDark ? Color.FromRgb(0x3A, 0x1F, 0x4D) : Color.FromRgb(0xE8, 0xB4, 0xD0), 0),
            });
        }

        rows.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        rows.Arrange(new Rect(rows.DesiredSize));
        rows.UpdateLayout();

        var bmp = new RenderTargetBitmap(
            (int)Math.Ceiling(rows.ActualWidth * 1.5), (int)Math.Ceiling(rows.ActualHeight * 1.5),
            144, 144, PixelFormats.Pbgra32);
        bmp.Render(rows);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(outputPath);
        encoder.Save(fs);
    }
}
