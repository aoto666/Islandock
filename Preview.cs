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
    /// <summary>README 用: 自分の Dock ではなく、Windows に最初から入っているアプリだけで並べる</summary>
    public static void RenderDemo(string outputPath)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sys = Environment.SystemDirectory;
        var demo = new List<DockEntry>
        {
            new() { Name = "エクスプローラー", Path = Path.Combine(win, "explorer.exe") },
            new() { Name = "メモ帳", Path = Path.Combine(sys, "notepad.exe") },
            new() { Name = "ペイント", Path = Path.Combine(sys, "mspaint.exe") },
            new() { Name = "タスク マネージャー", Path = Path.Combine(sys, "Taskmgr.exe") },
            new() { Name = "コマンド プロンプト", Path = Path.Combine(sys, "cmd.exe") },
            new() { Name = "PowerShell", Path = Path.Combine(sys, @"WindowsPowerShell\v1.0\powershell.exe") },
            new() { Name = "コントロール パネル", Path = Path.Combine(sys, "control.exe") },
        };
        Render(outputPath, demo.Where(e => File.Exists(e.Path)).ToList());
    }

    /// <summary>
    /// 紹介動画用: Windows 標準アプリの Dock アイコンを、1つずつ大きな透過 PNG に書き出す（icon0.png, icon1.png, ...）
    /// </summary>
    public static void ExportDemoIcons(string folder, bool dark)
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sys = Environment.SystemDirectory;
        var demo = new List<DockEntry>
        {
            new() { Name = "エクスプローラー", Path = Path.Combine(win, "explorer.exe") },
            new() { Name = "メモ帳", Path = Path.Combine(sys, "notepad.exe") },
            new() { Name = "ペイント", Path = Path.Combine(sys, "mspaint.exe") },
            new() { Name = "タスク マネージャー", Path = Path.Combine(sys, "Taskmgr.exe") },
            new() { Name = "コマンド プロンプト", Path = Path.Combine(sys, "cmd.exe") },
            new() { Name = "PowerShell", Path = Path.Combine(sys, @"WindowsPowerShell\v1.0\powershell.exe") },
            new() { Name = "コントロール パネル", Path = Path.Combine(sys, "control.exe") },
            new() { Name = "ごみ箱", Path = "::{645FF040-5081-101B-9F08-00AA002F954E}" },
        }.Where(e => e.Path.StartsWith("::") || File.Exists(e.Path)).ToList();

        Directory.CreateDirectory(folder);
        var theme = dark ? DockTheme.Dark : DockTheme.Light;
        const double size = 84, scale = 5;
        for (int i = 0; i < demo.Count; i++)
        {
            var item = new DockItem(demo[i], size, 0, isTrash: demo[i].Path.StartsWith("::"));
            item.ApplyStyle(theme, useTile: true);
            var host = new Border { Child = item.Visual };
            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();

            var bmp = new RenderTargetBitmap((int)Math.Ceiling(host.ActualWidth * scale), (int)Math.Ceiling(host.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(host);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(Path.Combine(folder, $"icon{i}.png"));
            encoder.Save(fs);
        }
    }

    public static void Render(string outputPath, List<DockEntry>? entriesOverride = null)
    {
        var entries = entriesOverride ?? DockConfig.Load();
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
