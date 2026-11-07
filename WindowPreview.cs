using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using static Islandock.NativeMethods;

namespace Islandock;

/// <summary>
/// Windows のタスクバーと同じように、Dock のアプリのアイコンにカーソルを乗せると出る
/// 「そのアプリのウィンドウの一覧（縮小画像）」。押したウィンドウを前に出す。× で閉じる。
/// 縮小画像は Windows（DWM）に直接描いてもらう。動いている中身がそのまま映り、しまってあるウィンドウも最後の見た目が出る。
/// </summary>
public sealed class WindowPreview : Window
{
    private const double ThumbWidth = 200;
    private const double ThumbHeight = 120;

    private readonly StackPanel _cards = new() { Orientation = Orientation.Horizontal };
    private readonly Action<IntPtr> _activate;
    private readonly DockTheme _theme;
    private readonly ImageSource? _appIcon;
    private readonly List<Thumb> _thumbs = new();
    private IntPtr _hwnd;
    private Point _anchor; // アイコンの上端の真ん中（DIP）

    /// <summary>縮小画像 1 つ分: 映すウィンドウ・描く場所・DWM の登録</summary>
    private sealed class Thumb
    {
        public IntPtr Source;
        public FrameworkElement Host = null!;
        public FrameworkElement Fallback = null!;
        public IntPtr Handle;
    }

    /// <summary>ウィンドウの一覧を出しているアプリ</summary>
    public DockItem Item { get; }

    public WindowPreview(DockItem item, IReadOnlyList<IntPtr> windows, DockTheme theme, Action<IntPtr> activate)
    {
        Item = item;
        _theme = theme;
        _activate = activate;
        _appIcon = IconHelper.GetIcon(item.Entry.Path, 32);

        // DWM の縮小画像は「透明なウィンドウ」には描かれないので、ふつうの（不透明な）ウィンドウにする
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = OpaqueOf(theme.LabelBackground);
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        Content = new Border { Child = _cards, Padding = new Thickness(6) };

        foreach (var h in windows) _cards.Children.Add(CreateCard(h));
        SizeChanged += (_, _) => Place();
        LayoutUpdated += (_, _) => UpdateThumbs();
        Closed += (_, _) =>
        {
            foreach (var t in _thumbs) Unregister(t);
            _thumbs.Clear();
        };
    }

    private static Brush OpaqueOf(Brush brush)
    {
        var c = brush is SolidColorBrush s ? s.Color : Colors.White;
        return new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Alt+Tab に出さない・押しても Dock から入力を奪わない
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        // Windows 11 の角丸と影
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        foreach (var t in _thumbs) Register(t);
        UpdateThumbs();
    }

    /// <summary>アイコンの上（top: アイコン上端の真ん中、DIP）に出す</summary>
    public void ShowAbove(Point top)
    {
        _anchor = top;
        if (!IsVisible) Show();
        Place();
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        double w = ActualWidth, h = ActualHeight;
        Left = Math.Clamp(_anchor.X - w / 2, area.Left, Math.Max(area.Left, area.Right - w));
        Top = _anchor.Y - h - 8;
    }

    // ===================== 縮小画像（DWM） =====================

    private void Register(Thumb t)
    {
        if (t.Handle != IntPtr.Zero || _hwnd == IntPtr.Zero) return;
        if (DwmRegisterThumbnail(_hwnd, t.Source, out t.Handle) != 0) t.Handle = IntPtr.Zero;
    }

    private static void Unregister(Thumb t)
    {
        if (t.Handle != IntPtr.Zero) DwmUnregisterThumbnail(t.Handle);
        t.Handle = IntPtr.Zero;
    }

    /// <summary>それぞれの縮小画像を、枠の中に縦横比を保って描く位置へ合わせる</summary>
    private void UpdateThumbs()
    {
        if (_hwnd == IntPtr.Zero) return;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        var toPx = source.CompositionTarget.TransformToDevice;

        foreach (var t in _thumbs)
        {
            if (t.Handle == IntPtr.Zero || !t.Host.IsVisible || DwmQueryThumbnailSourceSize(t.Handle, out var size) != 0 ||
                size.X <= 0 || size.Y <= 0)
            {
                t.Fallback.Visibility = Visibility.Visible;
                continue;
            }

            var topLeft = toPx.Transform(t.Host.TranslatePoint(new Point(0, 0), this));
            var bottomRight = toPx.Transform(t.Host.TranslatePoint(new Point(t.Host.ActualWidth, t.Host.ActualHeight), this));
            double boxW = bottomRight.X - topLeft.X, boxH = bottomRight.Y - topLeft.Y;
            double scale = Math.Min(boxW / size.X, boxH / size.Y);
            double w = size.X * scale, h = size.Y * scale;
            double x = topLeft.X + (boxW - w) / 2, y = topLeft.Y + (boxH - h) / 2;

            var props = new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = DWM_TNP_RECTDESTINATION | DWM_TNP_VISIBLE | DWM_TNP_OPACITY,
                rcDestination = new RECT
                {
                    Left = (int)Math.Round(x), Top = (int)Math.Round(y),
                    Right = (int)Math.Round(x + w), Bottom = (int)Math.Round(y + h),
                },
                opacity = 255,
                fVisible = true,
            };
            DwmUpdateThumbnailProperties(t.Handle, ref props);
            t.Fallback.Visibility = Visibility.Collapsed;
        }
    }

    // ===================== 1 枚分の見た目 =====================

    private FrameworkElement CreateCard(IntPtr hwnd)
    {
        var fg = _theme.LabelForeground;

        // 上段: アプリのアイコン・ウィンドウのタイトル・閉じるボタン
        var header = new DockPanel { Margin = new Thickness(2, 0, 0, 6), LastChildFill = true };
        var close = new Button
        {
            Content = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 9,
            Width = 22,
            Height = 22,
            Foreground = fg,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Visibility = Visibility.Hidden,
            Cursor = Cursors.Hand,
            Focusable = false,
            Template = CloseButtonTemplate(),
        };
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);

        var icon = new Image { Source = _appIcon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);

        string title = WindowTitle(hwnd);
        header.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = fg,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)Application.Current.FindResource("UiFont"),
        });

        // 下段: 縮小画像を描く場所（描けないときはアプリのアイコンを大きく）
        var fallback = new Image { Source = _appIcon, Width = 48, Height = 48 };
        RenderOptions.SetBitmapScalingMode(fallback, BitmapScalingMode.HighQuality);
        var thumbHost = new Grid { Width = ThumbWidth, Height = ThumbHeight };
        thumbHost.Children.Add(fallback);
        var thumb = new Thumb { Source = hwnd, Host = thumbHost, Fallback = fallback };
        _thumbs.Add(thumb);
        Register(thumb);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(thumbHost);

        var hover = new SolidColorBrush(_theme.IsDark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        var card = new Border
        {
            Child = stack,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Margin = new Thickness(2, 0, 2, 0),
            Cursor = Cursors.Hand,
            ToolTip = title,
        };
        card.MouseEnter += (_, _) => { card.Background = hover; close.Visibility = Visibility.Visible; };
        card.MouseLeave += (_, _) => { card.Background = Brushes.Transparent; close.Visibility = Visibility.Hidden; };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, close)) return;
            _activate(hwnd);
        };
        close.Click += (_, _) =>
        {
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Unregister(thumb);
            _thumbs.Remove(thumb);
            _cards.Children.Remove(card);
            if (_cards.Children.Count == 0) Close();
        };
        return card;
    }

    private static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x))
            if (x == parent) return true;
        return false;
    }

    private static ControlTemplate CloseButtonTemplate()
    {
        // 丸い背景（乗せると赤）に × を描くだけのボタン
        var border = new FrameworkElementFactory(typeof(Border), "bg");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var text = new FrameworkElementFactory(typeof(ContentPresenter));
        text.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        text.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(text);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var trigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)), "bg"));
        template.Triggers.Add(trigger);
        return template;
    }

    public static string WindowTitle(IntPtr h)
    {
        int length = GetWindowTextLength(h);
        if (length <= 0) return "";
        var sb = new System.Text.StringBuilder(length + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>見えないように隠されている（ストアアプリの中身など）か</summary>
    public static bool IsCloaked(IntPtr h) =>
        DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private const uint WM_CLOSE = 0x0010;
    private const int DWMWA_CLOAKED = 14;
    private const uint DWM_TNP_RECTDESTINATION = 0x1;
    private const uint DWM_TNP_OPACITY = 0x4;
    private const uint DWM_TNP_VISIBLE = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public uint dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int X, Y; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(IntPtr thumb);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr h, System.Text.StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr h);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
}
