using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Islandock;

/// <summary>
/// 開発用（紹介動画・README 用）: アイランドのいろいろな状態を、架空のデータで透過 PNG に書き出す。
/// この PC で実際に再生中の曲やデバイス名などは使わない。
/// </summary>
public partial class IslandWindow
{
    internal void ExportDemoStates(string folder)
    {
        Directory.CreateDirectory(folder);
        IslandRowShift.Y = 0;
        IslandRow.BeginAnimation(OpacityProperty, null);
        IslandRow.Opacity = 1;

        // ---- 架空の曲 ----
        _media.SetDemo(new MediaInfo
        {
            Title = "Sunset Drive",
            Artist = "Neon Coast",
            AppName = "メディア プレーヤー",
            Art = DemoArt(),
            Accent = Color.FromRgb(0xFF, 0x8A, 0x5C),
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(72),
            Duration = TimeSpan.FromSeconds(208),
            PositionAt = DateTime.Now,
            CanSeek = true,
            CanNext = true,
            CanPrevious = true,
        });

        // 小さいとき: 再生中（アルバムアートと波形）
        Snap(folder, "compact-media", Mode.Compact);

        // 大きいとき: 再生中
        _expanded = true;
        ShowTab(Tab.Media);
        _hudBar.Value = 0.6;
        Snap(folder, "media", Mode.Expanded);

        // タイマー動作中（大きい）
        _timerDuration = TimeSpan.FromMinutes(5);
        _timerLeft = TimeSpan.FromMinutes(5);
        _timerEndsAt = DateTime.Now + TimeSpan.FromSeconds(3 * 60 + 27.5);
        _timerStarted = true;
        _stopwatch.Restart();
        UpdateTimerButtons();
        ShowTab(Tab.Timer);
        Snap(folder, "timer", Mode.Expanded);

        // 小さいとき: タイマー + 右の丸に音楽（2つ同時）
        _expanded = false;
        _stopwatch.Reset();
        Snap(folder, "compact-split", Mode.Compact);

        // マイク / カメラのランプ
        Snap(folder, "compact-privacy", Mode.Compact, privacy: true);

        // ---- お知らせ ----
        var battery = BatteryWithText(82, Green);
        SnapAlert(folder, "alert-charging", () => ShowAlert("power", null, "充電中", titleBrush: new SolidColorBrush(Green),
            right: battery, width: 300, height: 40));
        SnapAlert(folder, "alert-volume", () => ShowAlert("volume", Glyph("", 16, Colors.White), "", center: _hudBar,
            right: new TextBlock { Text = "60", Foreground = Brushes.White, FontSize = 13, Width = 26, TextAlignment = TextAlignment.Right },
            width: 270, height: 40));
        SnapAlert(folder, "alert-mute", () => ShowAlert("mute", CircleGlyph("", Red), "消音 オン",
            titleBrush: new SolidColorBrush(Red), width: 230, height: 40));
        SnapAlert(folder, "alert-headphone", () => ShowAlert("device", CircleGlyph("", Color.FromRgb(0x0A, 0x84, 0xFF)),
            "ワイヤレス ヘッドホン", "音の出力先が切り替わりました", width: 360, height: 56));
        SnapAlert(folder, "alert-camera", () => ShowAlert("privacy", CircleGlyph("", Green), "カメラを使用中", "ビデオ通話",
            width: 320, height: 56));
        SnapAlert(folder, "alert-lowbattery", () => ShowAlert("power", BatteryIcon(15, Red), "バッテリー残量が少なくなっています", "残り 15%",
            width: 380, height: 58));
        SnapAlert(folder, "alert-received", () => ShowAlert("phone", CircleGlyph("", AirBlue), "ファイルを受信しました", "IMG_0412.jpg",
            width: 380, height: 56));
        SnapAlert(folder, "alert-timer", () =>
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(new Button
            {
                Content = "停止", Style = (Style)FindResource("PillButton"), Background = new SolidColorBrush(Orange),
                Foreground = Brushes.Black, FontWeight = FontWeights.SemiBold, Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(0, 0, 6, 0),
            });
            buttons.Children.Add(new Button { Content = "+1分", Style = (Style)FindResource("PillButton"), Padding = new Thickness(12, 6, 12, 6) });
            ShowAlert("timer-done", CircleGlyph("", Orange), "タイマー", "時間になりました", right: buttons, width: 380, height: 66, sticky: true);
        });
    }

    private void SnapAlert(string folder, string name, Action show)
    {
        _alert = null;
        _expanded = false;
        show();
        Snap(folder, name, Mode.Alert);
        _alert = null;
    }

    /// <summary>アニメーションを止めて、指定の状態の見た目にそろえてから書き出す</summary>
    private void Snap(string folder, string name, Mode mode, bool privacy = false)
    {
        UpdateCompact();
        UpdateLive();
        _mode = mode;
        var size = mode switch
        {
            Mode.Expanded => new Size(ExpandedWidth, ExpandedHeight),
            Mode.Alert => new Size(_alert!.Width, _alert.Height),
            _ => CompactSize(),
        };
        Island.BeginAnimation(WidthProperty, null);
        Island.BeginAnimation(HeightProperty, null);
        Island.Width = size.Width;
        Island.Height = size.Height;
        foreach (var (view, m) in new[] { (CompactView, Mode.Compact), (AlertView, Mode.Alert), (ExpandedView, Mode.Expanded) })
        {
            view.BeginAnimation(OpacityProperty, null);
            view.Visibility = m == mode ? Visibility.Visible : Visibility.Collapsed;
            view.Opacity = m == mode ? 1 : 0;
        }
        UpdateSideItems();
        if (privacy)
        {
            PrivacyDots.Visibility = CameraDot.Visibility = MicDot.Visibility = Visibility.Visible;
        }
        // 波形などのアニメーションは途中の高さで止める
        foreach (var bar in _compactBars.Concat(_mediaBars)) bar.BeginAnimation(HeightProperty, null);
        int i = 0;
        foreach (var bar in _compactBars.Concat(_mediaBars)) bar.Height = Math.Max(bar.Height, 4 + (i++ * 7 % 13));

        UpdateLayout();
        UpdateIslandShape();
        UpdateLayout();

        var root = (FrameworkElement)Content;
        double w = root.ActualWidth, h = root.ActualHeight;
        const double scale = 3;
        var bmp = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(root)
            {
                Stretch = Stretch.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, w, h),
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            }, null, new Rect(0, 0, w, h));
        }
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(Path.Combine(folder, name + ".png"));
        encoder.Save(fs);
    }

    /// <summary>架空のアルバムアート（夕焼けのグラデーションと太陽）</summary>
    private static BitmapSource DemoArt()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2B, 0x10, 0x55), Color.FromRgb(0xF7, 0x9B, 0x4B), 90), null, new Rect(0, 0, 300, 300));
            dc.DrawEllipse(new LinearGradientBrush(Color.FromRgb(0xFF, 0xE0, 0x7A), Color.FromRgb(0xFF, 0x5E, 0x7E), 90), null, new Point(150, 170), 78, 78);
            for (int k = 0; k < 5; k++)
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x18, 0x5E)), null, new Rect(0, 178 + k * 16, 300, 5 + k * 2));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2B, 0x10, 0x55)), null, new Rect(0, 250, 300, 50));
        }
        var bmp = new RenderTargetBitmap(300, 300, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }
}
