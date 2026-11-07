using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using static Islandock.NativeMethods;

namespace Islandock;

/// <summary>時計ウィジェットの設定（%APPDATA%\Islandock\clock.json）</summary>
public class ClockSettings
{
    public bool Visible { get; set; } = true;

    /// <summary>Clear（透明）/ Light / Dark / Auto（Windows のテーマに合わせる）</summary>
    public string Style { get; set; } = "Clear";

    /// <summary>ウィジェットの一辺（ピクセル）</summary>
    public double Size { get; set; } = 170;

    /// <summary>位置（NaN なら右上に自動配置）</summary>
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;

    /// <summary>このバージョンが知らない項目。保存するときに消さずにそのまま残す</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public static class ClockConfig
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static string ConfigPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Islandock", "clock.json");

    public static ClockSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<ClockSettings>(File.ReadAllText(ConfigPath), Options) ?? new ClockSettings();
        }
        catch
        {
        }
        return new ClockSettings();
    }

    public static void Save(ClockSettings settings)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
        }
    }
}

/// <summary>
/// macOS 風のアナログ時計ウィジェット。デスクトップに置かれ、他のウィンドウの後ろに居続ける。
/// 「透明」スタイルは macOS 26 のクリアなウィジェットに寄せた見た目。
/// </summary>
public sealed class ClockWidget : Window
{
    private const double ShadowPad = 24; // 影が切れないための余白（台紙の影は大きめ）

    private static ClockWidget? _instance;

    private readonly ClockSettings _settings;
    private readonly Canvas _canvas = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly RotateTransform _hourRot = new();
    private readonly RotateTransform _minuteRot = new();
    private readonly RotateTransform _secondRot = new();
    private int _lastSyncedMinute = -1;
    private IntPtr _hwnd;

    // ---- 外から呼ぶ用 ----

    public static bool IsShown => _instance != null;

    /// <summary>起動時に呼ぶ。設定で表示になっていれば出す</summary>
    public static void ShowIfEnabled()
    {
        if (ClockConfig.Load().Visible) ShowWidget();
    }

    public static void ShowWidget()
    {
        if (_instance != null) return;
        _instance = new ClockWidget(ClockConfig.Load());
        _instance.Show();
        _instance.SaveVisible(true);
    }

    /// <summary>ライト / ダークの設定が変わったときに描き直す</summary>
    public static void RefreshTheme() => _instance?.Build();

    public static void HideWidget()
    {
        if (_instance == null) return;
        _instance.SaveVisible(false);
        _instance.Close();
    }

    // ---- 本体 ----

    private ClockWidget(ClockSettings settings)
    {
        _settings = settings;
        _settings.Size = Math.Clamp(_settings.Size, 100, 400);

        Title = "Islandock Clock";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;

        Content = new Grid { Margin = new Thickness(ShadowPad), Children = { _canvas } };
        Build();
        PlaceWindow();

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); } catch { }
            _settings.Left = Left;
            _settings.Top = Top;
            ClockConfig.Save(_settings);
            SendToBottom();
        };
        MouseRightButtonUp += (_, e) => { ShowMenu(); e.Handled = true; };
        Activated += (_, _) => SendToBottom();

        _timer.Tick += (_, _) => UpdateHands();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemEvents.TimeChanged += OnTimeChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Alt+Tab に出さない
        _hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        SendToBottom();
        UpdateHands(forceSync: true);
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.TimeChanged -= OnTimeChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        if (_instance == this) _instance = null;
        base.OnClosed(e);
    }

    private void SaveVisible(bool visible)
    {
        _settings.Visible = visible;
        ClockConfig.Save(_settings);
    }

    /// <summary>デスクトップのウィジェットらしく、常に他のウィンドウの後ろに置く</summary>
    private void SendToBottom()
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private void PlaceWindow()
    {
        Width = Height = _settings.Size + ShadowPad * 2;

        var area = SystemParameters.WorkArea;
        double left = _settings.Left, top = _settings.Top;
        if (double.IsNaN(left) || double.IsNaN(top))
        {
            left = area.Right - Width - 24 + ShadowPad;
            top = area.Top + 64 - ShadowPad;
        }

        // 画面の外に行ってしまっていたら戻す
        double vl = SystemParameters.VirtualScreenLeft, vt = SystemParameters.VirtualScreenTop;
        left = Math.Clamp(left, vl - ShadowPad, vl + SystemParameters.VirtualScreenWidth - Width + ShadowPad);
        top = Math.Clamp(top, vt - ShadowPad, vt + SystemParameters.VirtualScreenHeight - Height + ShadowPad);
        Left = left;
        Top = top;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General && _settings.Style == "Auto")
            Dispatcher.Invoke(Build);
    }

    private void OnTimeChanged(object? sender, EventArgs e) =>
        Dispatcher.Invoke(() => UpdateHands(forceSync: true));

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Dispatcher.Invoke(() => UpdateHands(forceSync: true));
    }

    // ===== 針の動き =====

    private void UpdateHands(bool forceSync = false)
    {
        var now = DateTime.Now;
        double sec = now.Second + now.Millisecond / 1000.0;
        double min = now.Minute + sec / 60.0;
        double hour = now.Hour % 12 + min / 60.0;

        _minuteRot.Angle = min * 6;
        _hourRot.Angle = hour * 30;

        // 秒針は 60 秒で 1 周するアニメーションでなめらかに回す。ずれないよう毎分合わせ直す
        if (forceSync || now.Minute != _lastSyncedMinute)
        {
            _lastSyncedMinute = now.Minute;
            double start = sec * 6;
            _secondRot.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(start, start + 360, TimeSpan.FromSeconds(60))
            {
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
    }

    // ===== 見た目 =====

    private sealed record Palette(
        Brush Face, Brush? Rim, Brush Numbers, Brush MajorTick, Brush MinorTick, Brush Hand,
        Brush Second, Brush CenterDot, bool ElementShadow, bool FaceShadow,
        Brush Plate, Brush PlateRim);

    private static LinearGradientBrush Vertical(uint top, uint bottom)
    {
        static Color C(uint v) => Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        var b = new LinearGradientBrush(C(top), C(bottom), 90);
        b.Freeze();
        return b;
    }

    private static SolidColorBrush B(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    private static readonly Brush Orange = B(0xFFFF9500); // macOS のシステムオレンジ

    private static Palette GetPalette(string style)
    {
        if (style == "Auto") style = DockTheme.IsDarkMode() ? "Dark" : "Light";

        switch (style)
        {
            case "Light":
                return new Palette(B(0xFFFFFFFF), B(0x14000000), B(0xFF000000), B(0xFF1C1C1E), B(0x59000000),
                    B(0xFF000000), Orange, B(0xFFFFFFFF), ElementShadow: false, FaceShadow: true,
                    Plate: Vertical(0xC8F5F5F7, 0xB8E8E8ED), PlateRim: Vertical(0x99FFFFFF, 0x26FFFFFF));
            case "Dark":
                return new Palette(B(0xFF1C1C1E), B(0x1FFFFFFF), B(0xFFFFFFFF), B(0xFFF2F2F7), B(0x66FFFFFF),
                    B(0xFFFFFFFF), Orange, B(0xFF1C1C1E), ElementShadow: false, FaceShadow: true,
                    Plate: Vertical(0xC03A3A3C, 0xB82C2C2E), PlateRim: Vertical(0x40FFFFFF, 0x0FFFFFFF));
            default: // Clear：透明なガラスの板の上に、透明な文字盤を載せる（macOS 26 のクリアなウィジェット風）
                return new Palette(B(0x12FFFFFF), Vertical(0x66FFFFFF, 0x1AFFFFFF), B(0xFFFFFFFF), B(0xF2FFFFFF), B(0x8CFFFFFF),
                    B(0xFFFFFFFF), Orange, B(0xFFFFFFFF), ElementShadow: true, FaceShadow: false,
                    Plate: Vertical(0x40FFFFFF, 0x24FFFFFF), PlateRim: Vertical(0xCCFFFFFF, 0x59FFFFFF));
        }
    }

    private void Build()
    {
        DrawClock(_canvas, _settings.Size, GetPalette(_settings.Style), _hourRot, _minuteRot, _secondRot);
    }

    /// <summary>
    /// ウィジェット全体を描く：角の丸いガラスの板（macOS のウィジェットの台紙）＋その上の時計
    /// </summary>
    private static void DrawClock(Canvas canvas, double widgetSize, Palette p,
        RotateTransform hourRot, RotateTransform minuteRot, RotateTransform secondRot)
    {
        canvas.Children.Clear();
        canvas.Width = canvas.Height = widgetSize;

        // ---- 台紙（macOS のウィジェットと同じく、一辺の約 22% の角丸） ----
        double radius = widgetSize * 0.225;
        canvas.Children.Add(new Rectangle
        {
            Width = widgetSize, Height = widgetSize,
            RadiusX = radius, RadiusY = radius,
            Fill = p.Plate,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.22 },
        });
        // 明るい壁紙でも枠の形が分かるよう、外側にごく薄い暗い輪郭線
        canvas.Children.Add(Positioned(new Rectangle
        {
            Width = widgetSize + 1, Height = widgetSize + 1,
            RadiusX = radius + 0.5, RadiusY = radius + 0.5,
            Stroke = B(0x24000000),
            StrokeThickness = 1,
            IsHitTestVisible = false,
        }, -0.5, -0.5));
        // 上が明るく下に向かって少し弱まる、ガラスの縁の光
        double rimWidth = Math.Max(1.5, widgetSize / 115);
        canvas.Children.Add(Positioned(new Rectangle
        {
            Width = widgetSize - rimWidth, Height = widgetSize - rimWidth,
            RadiusX = radius - rimWidth / 2, RadiusY = radius - rimWidth / 2,
            Stroke = p.PlateRim,
            StrokeThickness = rimWidth,
            IsHitTestVisible = false,
        }, rimWidth / 2, rimWidth / 2));

        // ---- 時計は台紙の内側に余白を取って載せる ----
        double inset = widgetSize * 0.075;
        var clock = new Canvas();
        DrawFace(clock, widgetSize - inset * 2, p, hourRot, minuteRot, secondRot);
        canvas.Children.Add(Positioned(clock, inset, inset));
    }

    private static void DrawFace(Canvas canvas, double size, Palette p,
        RotateTransform hourRot, RotateTransform minuteRot, RotateTransform secondRot)
    {
        canvas.Width = canvas.Height = size;

        double c = size / 2;
        double r = size / 2;

        // ---- 文字盤 ----
        var face = new Ellipse
        {
            Width = size, Height = size,
            Fill = p.Face,
            Stroke = p.Rim,
            StrokeThickness = p.Rim == null ? 0 : Math.Max(1, size / 170),
        };
        if (p.FaceShadow)
            face.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.28 };
        canvas.Children.Add(face);

        var dial = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
        var hands = new Canvas { Width = size, Height = size, IsHitTestVisible = false };
        if (p.ElementShadow)
        {
            // 透明スタイルでは、どんな壁紙でも読めるように白い要素に薄い影を付ける
            dial.Effect = new DropShadowEffect { BlurRadius = 5, ShadowDepth = 0.6, Direction = 270, Opacity = 0.45 };
            hands.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1.2, Direction = 270, Opacity = 0.45 };
        }
        canvas.Children.Add(dial);
        canvas.Children.Add(hands);

        // ---- 目盛り（60 本。5 分ごとは太く長く） ----
        double k = size / 170; // 170px を基準にした倍率
        for (int i = 0; i < 60; i++)
        {
            bool major = i % 5 == 0;
            double outer = r - 7 * k;
            double inner = outer - (major ? 7 : 4) * k;
            double a = i * Math.PI / 30;
            double sx = Math.Sin(a), cy = -Math.Cos(a);
            dial.Children.Add(new Line
            {
                X1 = c + sx * inner, Y1 = c + cy * inner,
                X2 = c + sx * outer, Y2 = c + cy * outer,
                Stroke = major ? p.MajorTick : p.MinorTick,
                StrokeThickness = (major ? 1.8 : 1.0) * k,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }

        // ---- 数字 1〜12 ----
        var font = new FontFamily("SF Pro Rounded, Segoe UI Variable Display, Segoe UI");
        double fontSize = 19 * k;
        double numR = r - 26 * k;
        for (int h = 1; h <= 12; h++)
        {
            double a = h * Math.PI / 6;
            var ft = new FormattedText(h.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface(font, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
                fontSize, p.Numbers, 1.0);
            var geo = ft.BuildGeometry(new Point(0, 0));
            var bounds = geo.Bounds; // 字の見た目の中心で揃える
            double x = c + Math.Sin(a) * numR - (bounds.Left + bounds.Width / 2);
            double y = c - Math.Cos(a) * numR - (bounds.Top + bounds.Height / 2);
            geo.Transform = new TranslateTransform(x, y);
            dial.Children.Add(new System.Windows.Shapes.Path { Data = geo, Fill = p.Numbers });
        }

        // ---- 時針・分針（中心から細い軸 → 太い棒） ----
        hands.Children.Add(MakeHand(c, length: r * 0.50, width: 6.2 * k, stem: 13 * k, k, p.Hand, hourRot));
        hands.Children.Add(MakeHand(c, length: r * 0.80, width: 6.2 * k, stem: 13 * k, k, p.Hand, minuteRot));
        hands.Children.Add(Dot(c, 4.2 * k, p.Hand));

        // ---- 秒針（オレンジ。少し後ろにはみ出す） ----
        var second = new Canvas { Width = size, Height = size };
        second.Children.Add(new Line
        {
            X1 = c, Y1 = c + r * 0.17,
            X2 = c, Y2 = c - r * 0.86,
            Stroke = p.Second,
            StrokeThickness = 1.4 * k,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        second.Children.Add(Dot(c, 3.4 * k, p.Second));
        second.Children.Add(Dot(c, 1.3 * k, p.CenterDot));
        secondRot.CenterX = secondRot.CenterY = c;
        second.RenderTransform = secondRot;
        hands.Children.Add(second);
    }

    private static Canvas MakeHand(double c, double length, double width, double stem, double k, Brush fill, RotateTransform rot)
    {
        var hand = new Canvas();
        double stemWidth = 2.2 * k;
        hand.Children.Add(Positioned(new Rectangle { Width = stemWidth, Height = stem + width / 2, Fill = fill },
            c - stemWidth / 2, c - stem - width / 2));
        hand.Children.Add(Positioned(new Rectangle
        {
            Width = width, Height = length - stem, Fill = fill,
            RadiusX = width / 2, RadiusY = width / 2,
        }, c - width / 2, c - length));
        rot.CenterX = rot.CenterY = c;
        hand.RenderTransform = rot;
        return hand;
    }

    private static Ellipse Dot(double c, double radius, Brush fill) =>
        Positioned(new Ellipse { Width = radius * 2, Height = radius * 2, Fill = fill }, c - radius, c - radius);

    private static T Positioned<T>(T element, double left, double top) where T : UIElement
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        return element;
    }

    // ===== 右クリックメニュー =====

    private void ShowMenu()
    {
        DockTheme.Current().ApplyMenuResources();
        var menu = new ContextMenu();

        foreach (var (label, style) in new[] { ("透明", "Clear"), ("ライト", "Light"), ("ダーク", "Dark"), ("自動（ライト / ダークに合わせる）", "Auto") })
        {
            var item = new MenuItem { Header = label, IsChecked = _settings.Style == style };
            item.Click += (_, _) =>
            {
                _settings.Style = style;
                ClockConfig.Save(_settings);
                Build();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        foreach (var (label, size) in new[] { ("小", 140.0), ("中", 170.0), ("大", 220.0) })
        {
            var item = new MenuItem { Header = $"サイズ: {label}", IsChecked = Math.Abs(_settings.Size - size) < 0.5 };
            item.Click += (_, _) =>
            {
                // 中心の位置を保ったままサイズを変える
                double cx = Left + Width / 2, cy = Top + Height / 2;
                _settings.Size = size;
                _settings.Left = cx - (size / 2 + ShadowPad);
                _settings.Top = cy - (size / 2 + ShadowPad);
                ClockConfig.Save(_settings);
                Build();
                PlaceWindow();
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "ウィジェットを削除" };
        remove.Click += (_, _) => HideWidget();
        menu.Items.Add(remove);

        ContextMenu = menu;
        menu.IsOpen = true;
    }

    // ===== 開発用プレビュー =====

    /// <summary>Islandock.exe --preview-clock 出力.png ： 各スタイルを壁紙風の背景に並べて書き出す</summary>
    public static void RenderPreview(string outputPath)
    {
        const double size = 170;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var time = new DateTime(2026, 1, 1, 10, 9, 30);
        foreach (var style in new[] { "Clear", "Light", "Dark" })
        {
            foreach (var dark in new[] { false, true })
            {
                var canvas = new Canvas();
                var hr = new RotateTransform((time.Hour % 12 + time.Minute / 60.0) * 30);
                var mr = new RotateTransform(time.Minute * 6 + time.Second * 0.1);
                var sr = new RotateTransform(time.Second * 6);
                DrawClock(canvas, size, GetPalette(style), hr, mr, sr);
                row.Children.Add(new Border
                {
                    Padding = new Thickness(22),
                    Child = canvas,
                    Background = new LinearGradientBrush(
                        dark ? Color.FromRgb(0x1C, 0x2B, 0x4A) : Color.FromRgb(0x8E, 0xB8, 0xE8),
                        dark ? Color.FromRgb(0x3A, 0x1F, 0x4D) : Color.FromRgb(0xE8, 0xB4, 0xD0), 45),
                });
            }
        }

        row.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        row.Arrange(new Rect(row.DesiredSize));
        row.UpdateLayout();

        var bmp = new RenderTargetBitmap((int)Math.Ceiling(row.ActualWidth * 1.5), (int)Math.Ceiling(row.ActualHeight * 1.5),
            144, 144, PixelFormats.Pbgra32);
        bmp.Render(row);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(outputPath);
        encoder.Save(fs);
    }
}
