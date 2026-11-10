using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static Islandock.NativeMethods;

namespace Islandock;

/// <summary>
/// 画面上部の Dynamic Island。
/// 小さいときは時刻と日付。マウスを乗せると「ぬるっ」と広がって 再生中 / タイマー / カレンダー / ファイル共有 を表示する。
/// </summary>
public partial class IslandWindow : Window
{
    // ===== 大きさ・タイミングの設定 =====
    private const double CompactWidth = 220, CompactHeight = 36;
    private const double ExpandedWidth = 520, ExpandedHeight = 400;
    private const double MaxCornerRadius = 34;
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(120); // 乗せてから開くまで
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(450); // 離れてから閉じるまで
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(350);  // 小さくなってから隠れるまで
    private const double HiddenOffset = -60; // 隠れているときの位置（影ごと画面の外へ）

    private static readonly Brush ChipActive = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF7));
    private static readonly Brush ChipInactive = Frozen(Color.FromRgb(0x2C, 0x2C, 0x2E));
    private static readonly Brush TodayRed = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));

    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _hoverTimer = new() { Interval = HoverDelay };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = LeaveDelay };
    private readonly DispatcherTimer _hideTimer = new() { Interval = HideDelay };
    private bool _revealed;
    private DateTime _shownMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private bool _expanded;
    private Tab? _hoverTab;

    private enum Tab { Media, Timer, Calendar, AirDrop }
    private Tab _tab = Tab.Calendar;

    public IslandWindow()
    {
        InitializeComponent();
        Loc.Apply(this);
        PlaceWindow();
        InitLive();
        InitAirDrop();
        InitEdgeWatch();
        BuildWeekHeader();
        RenderCalendar();
        UpdateBgmButtons();
        ShowTab(Tab.Calendar);
        ApplyFeatureSettings();
        App.SettingsChanged += ApplyFeatureSettings;

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateClock();
        _clock.Start();
        UpdateClock();

        // --- マウスを乗せるだけで開く / 離れると閉じる ---
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            if (CompactPlayButton.IsMouseOver) return; // 小さいままの再生ボタンを押せるように
            if (Island.IsMouseOver || HoverStrip.IsMouseOver || Bubble.IsMouseOver) Expand(_hoverTab);
        };
        CompactPlayButton.MouseEnter += (_, _) => _hoverTimer.Stop();
        CompactPlayButton.MouseLeave += (_, _) => { if (!_expanded && Island.IsMouseOver) _hoverTimer.Start(); };
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            if (!Island.IsMouseOver && !IsTyping && !PinnedOpen) Collapse();
        };
        // --- マウスが上の端に来たときだけ表示する ---
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!ShouldReveal) SetRevealed(false);
        };
        foreach (var zone in new FrameworkElement[] { IslandRow, HoverStrip })
        {
            zone.MouseEnter += (_, _) => UpdateReveal();
            zone.MouseLeave += (_, _) => UpdateReveal();
        }

        foreach (var zone in new FrameworkElement[] { Island, HoverStrip, Bubble })
        {
            zone.MouseEnter += (_, _) =>
            {
                _leaveTimer.Stop();
                // 小さな丸に乗せたときは、その丸が表しているものを開く
                _hoverTab = zone == Bubble ? SecondaryTab() : null;
                if (!_expanded) _hoverTimer.Start();
            };
            zone.MouseLeave += (_, _) =>
            {
                _hoverTimer.Stop();
                if (_expanded) _leaveTimer.Start();
            };
        }
        Island.MouseLeftButtonUp += (_, _) => { if (!_expanded) Expand(); };
        Bubble.MouseLeftButtonUp += (_, _) => { if (!_expanded) Expand(SecondaryTab()); };

        // 他のウィンドウへ移ったら閉じる
        Deactivated += (_, _) => { if (!Island.IsMouseOver && !PinnedOpen) Collapse(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Collapse(); };
        Island.SizeChanged += (_, _) => UpdateIslandShape();
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.WorkArea)) Dispatcher.Invoke(PlaceWindow);
        };
    }

    /// <summary>BGM の URL を入力している最中か（このときはマウスが離れても閉じない）</summary>
    private bool IsTyping => IsActive && BgmUrlBox.IsKeyboardFocusWithin;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Alt+Tab の一覧に出さない
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        SetClickThrough(!_revealed);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void PlaceWindow()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top;
    }

    // ===== 時計 =====

    private void UpdateClock()
    {
        var now = DateTime.Now;
        CompactTime.Text = now.ToString("H:mm");
        BigTime.Text = now.ToString("H:mm");
        BigDate.Text = now.ToString(Loc.DateFormat("yyyy年M月d日 dddd"), Loc.Culture);
        CompactRight.Text = now.ToString(Loc.DateFormat("M月d日 (ddd)"), Loc.Culture);

        // 日付が変わったらカレンダーの「今日」も更新
        if (DayGrid.Tag is DateTime rendered && rendered != DateTime.Today) RenderCalendar();

        UpdateCompact();
    }

    // ===== 広がる・縮むアニメーション =====

    /// <summary>大きく広げる。tab を省略すると、いま動いているもの（音楽・タイマー）のタブを開く</summary>
    private void Expand(Tab? tab = null)
    {
        if (_expanded || _alert?.Sticky == true) return;
        if (FirstEnabledTab() == null) return; // 開いて見せるものがない（設定ですべてオフ）
        if (_alert != null) DismissAlert();
        _expanded = true;

        ShowTab(tab ?? MainTab() ?? _tab);
        ApplyState();
        UpdateClock();
        UpdateLive();
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        _hoverTimer.Stop();
        _leaveTimer.Stop();
        if (BgmEditor.Visibility == Visibility.Visible) ShowBgmEditor(false); // 入力途中で閉じたら取り消し
        ApplyState();
    }

    // ===== 上の端に来たときだけ表示 =====

    /// <summary>開いている・お知らせ中・マウスが乗っている・ファイルをドラッグ中なら表示する</summary>
    private bool ShouldReveal =>
        _expanded || _alert != null || _dragOver || _edgeReveal || !_frontMaximized || (MediaVisible && !_topCovered)
        || IslandRow.IsMouseOver || HoverStrip.IsMouseOver;

    // ===== 上の端に「しばらく止めた」ときだけ出す（ブラウザのタブを邪魔しない） =====
    private static readonly TimeSpan EdgeDwell = TimeSpan.FromMilliseconds(500); // 端で止めてから出るまで
    private const int EdgeMoveTolerance = 40; // 端に沿って横に動かしたら（タブ選び中とみなして）やり直し
    private readonly DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private DateTime? _edgeSince;
    private int _edgeStartX;
    private bool _edgeReveal, _topCovered, _wasPressed;
    private bool _frontMaximized = true; // 最初の確認で必ず表示 / 非表示が反映されるように

    private void InitEdgeWatch()
    {
        _edgeTimer.Tick += (_, _) => WatchEdge();
        _edgeTimer.Start();
    }

    private void WatchEdge()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetCursorPos(out var pt) || !GetWindowRect(hwnd, out var win)) return;
        double scale = (win.Right - win.Left) / Math.Max(1, ActualWidth);
        int center = (win.Left + win.Right) / 2;

        // 最大化したブラウザなどが画面の上を覆っているときは、再生中でも隠しておく
        bool covered = IsTopCovered(win, center, scale);
        if (covered != _topCovered) { _topCovered = covered; UpdateReveal(); }

        // 手前のアプリが最大化・全画面でなければ、常に表示しておく
        bool maximized = IsFrontAppMaximized();
        if (maximized != _frontMaximized) { _frontMaximized = maximized; UpdateReveal(); }

        bool pressed = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        bool click = pressed && !_wasPressed;
        _wasPressed = pressed;

        bool atEdge = pt.Y <= win.Top + 2 && Math.Abs(pt.X - center) <= HoverStrip.Width / 2 * scale;
        if (!atEdge || click || (_edgeSince != null && Math.Abs(pt.X - _edgeStartX) > EdgeMoveTolerance * scale))
        {
            _edgeSince = null;
        }
        if (atEdge && _edgeSince == null && !click)
        {
            _edgeSince = DateTime.Now;
            _edgeStartX = pt.X;
        }

        if (!_edgeReveal && _edgeSince != null && DateTime.Now - _edgeSince >= EdgeDwell)
        {
            _edgeReveal = true;
            UpdateReveal();
        }
        else if (_edgeReveal && !atEdge && !IslandRow.IsMouseOver && !HoverStrip.IsMouseOver)
        {
            _edgeReveal = false;
            UpdateReveal();
        }
    }

    /// <summary>手前のウィンドウ（最大化したブラウザなど）が、アイランドのある画面上部を覆っているか</summary>
    private static bool IsTopCovered(RECT win, int center, double scale)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !GetWindowRect(fg, out var r)) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == Environment.ProcessId) return false;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(fg, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // デスクトップ・タスクバー
        return r.Top <= win.Top + 4 * scale && r.Bottom > win.Top + 60 * scale && r.Left < center && r.Right > center;
    }

    /// <summary>隠れている間はウィンドウ全体をクリックが素通りするようにする</summary>
    private void SetClickThrough(bool on)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT);
    }

    /// <summary>状態に合わせて表示 / 非表示を切り替える（隠すときは少し待ってから）</summary>
    private void UpdateReveal()
    {
        if (ShouldReveal)
        {
            _hideTimer.Stop();
            SetRevealed(true);
        }
        else if (_revealed && !_hideTimer.IsEnabled)
        {
            _hideTimer.Start();
        }
    }

    private void SetRevealed(bool show)
    {
        if (show == _revealed) return;
        _revealed = show;
        SetClickThrough(!show);
        var duration = TimeSpan.FromSeconds(show ? 0.35 : 0.3);
        IEasingFunction ease = show
            ? new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
            : new CubicEase { EasingMode = EasingMode.EaseIn };
        IslandRowShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(show ? 0 : HiddenOffset, duration) { EasingFunction = ease });
        IslandRow.BeginAnimation(OpacityProperty,
            new DoubleAnimation(show ? 1 : 0, TimeSpan.FromSeconds(show ? 0.2 : 0.3)));
    }

    private void AnimateSize(double width, double height, double seconds, IEasingFunction ease)
    {
        var duration = TimeSpan.FromSeconds(seconds);
        Island.BeginAnimation(WidthProperty, new DoubleAnimation(width, duration) { EasingFunction = ease });
        Island.BeginAnimation(HeightProperty, new DoubleAnimation(height, duration) { EasingFunction = ease });
    }

    private static void Fade(UIElement element, double to, double seconds, double delay, Action? completed = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = TimeSpan.FromSeconds(delay),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        if (completed != null) anim.Completed += (_, _) => completed();
        element.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>大きさに合わせて角の丸みと切り抜きを更新する（小さいときは完全なカプセル形）</summary>
    private void UpdateIslandShape()
    {
        double w = Island.ActualWidth, h = Island.ActualHeight;
        double r = Math.Min(h / 2, MaxCornerRadius);
        Island.CornerRadius = new CornerRadius(r);
        Island.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    // ===== タブ =====

    private void OnMediaTab(object sender, RoutedEventArgs e) => ShowTab(Tab.Media);
    private void OnTimerTab(object sender, RoutedEventArgs e) => ShowTab(Tab.Timer);
    private void OnCalendarTab(object sender, RoutedEventArgs e) => ShowTab(Tab.Calendar);

    // ===== 設定（機能ごとのオン / オフ） =====

    private static IslandSettings Features => IslandConfig.Load();

    private static bool TabEnabled(Tab tab) => tab switch
    {
        Tab.Media => Features.ShowMedia,
        Tab.Timer => Features.ShowTimer,
        Tab.Calendar => Features.ShowCalendar,
        _ => Features.ShowFileShare,
    };

    private static Tab? FirstEnabledTab() =>
        new[] { Tab.Calendar, Tab.Media, Tab.Timer, Tab.AirDrop }.Where(TabEnabled).Cast<Tab?>().FirstOrDefault();

    /// <summary>設定画面での変更をタブ・小さいときの表示・ランプに反映する</summary>
    private void ApplyFeatureSettings()
    {
        MediaTab.Visibility = Features.ShowMedia ? Visibility.Visible : Visibility.Collapsed;
        TimerTab.Visibility = Features.ShowTimer ? Visibility.Visible : Visibility.Collapsed;
        CalendarTab.Visibility = Features.ShowCalendar ? Visibility.Visible : Visibility.Collapsed;
        AirDropTab.Visibility = Features.ShowFileShare ? Visibility.Visible : Visibility.Collapsed;

        if (FirstEnabledTab() == null) Collapse();
        else if (!TabEnabled(_tab)) ShowTab(_tab);
        UpdateCompact();
    }

    private void ShowTab(Tab tab)
    {
        if (!TabEnabled(tab)) tab = FirstEnabledTab() ?? tab;
        _tab = tab;
        MediaView.Visibility = tab == Tab.Media ? Visibility.Visible : Visibility.Collapsed;
        TimerView.Visibility = tab == Tab.Timer ? Visibility.Visible : Visibility.Collapsed;
        CalendarView.Visibility = tab == Tab.Calendar ? Visibility.Visible : Visibility.Collapsed;
        AirDropView.Visibility = tab == Tab.AirDrop ? Visibility.Visible : Visibility.Collapsed;
        StyleChip(MediaTab, tab == Tab.Media);
        StyleChip(TimerTab, tab == Tab.Timer);
        StyleChip(CalendarTab, tab == Tab.Calendar);
        StyleChip(AirDropTab, tab == Tab.AirDrop);
        if (tab == Tab.AirDrop) UpdateAirDropStatus();
        UpdateLive();
    }

    private static void StyleChip(Button chip, bool active)
    {
        chip.Background = active ? ChipActive : ChipInactive;
        chip.Foreground = active ? Brushes.Black : Brushes.White;
    }

    // ===== カレンダー =====

    private void BuildWeekHeader()
    {
        string[] names = Loc.WeekdayHeaders();
        foreach (var n in names)
        {
            WeekHeader.Children.Add(new TextBlock
            {
                Text = n,
                FontSize = 11.5,
                Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
    }

    private void RenderCalendar()
    {
        DayGrid.Children.Clear();
        DayGrid.Tag = DateTime.Today;
        MonthTitle.Text = _shownMonth.ToString(Loc.DateFormat("yyyy年M月"), Loc.Culture);

        // 表示する最初の日（その月の1日を含む週の日曜日）
        var start = _shownMonth.AddDays(-(int)_shownMonth.DayOfWeek);
        for (int i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            bool inMonth = day.Month == _shownMonth.Month;
            bool isToday = day == DateTime.Today;

            Brush fg = isToday ? Brushes.White
                : !inMonth ? new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A))
                : day.DayOfWeek == DayOfWeek.Sunday ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x60))
                : day.DayOfWeek == DayOfWeek.Saturday ? new SolidColorBrush(Color.FromRgb(0x64, 0xA8, 0xFF))
                : new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF7));

            DayGrid.Children.Add(new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Background = isToday ? TodayRed : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = day.Day.ToString(),
                    Foreground = fg,
                    FontSize = 13.5,
                    FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
    }

    private void OnPrevMonth(object sender, RoutedEventArgs e) { _shownMonth = _shownMonth.AddMonths(-1); RenderCalendar(); }
    private void OnNextMonth(object sender, RoutedEventArgs e) { _shownMonth = _shownMonth.AddMonths(1); RenderCalendar(); }
    private void OnToday(object sender, RoutedEventArgs e)
    {
        _shownMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        RenderCalendar();
    }

    // ===== 開発用 =====

    /// <summary>アニメーションなしで指定の状態にして、アイランドを PNG に書き出す</summary>
    internal void RenderPreview(string path, string mode)
    {
        // ふだんは画面の上に隠れているので、書き出すときは見える位置に出しておく
        IslandRow.Opacity = 1;
        IslandRowShift.Y = 0;
        if (mode != "compact")
        {
            _expanded = true;
            Island.Width = ExpandedWidth;
            Island.Height = ExpandedHeight;
            CompactView.Opacity = 0;
            ExpandedView.Visibility = Visibility.Visible;
            ExpandedView.Opacity = 1;
            ShowTab(mode switch
            {
                "media" => Tab.Media,
                "timer" => Tab.Timer,
                "airdrop" or "phone" => Tab.AirDrop,
                _ => Tab.Calendar,
            });
            if (mode == "phone")
            {
                OnPhoneStart(this, new RoutedEventArgs());
            }
        }
        UpdateLayout();
        UpdateIslandShape();
        UpdateLayout();

        var root = (FrameworkElement)Content;
        double w = root.ActualWidth, h = root.ActualHeight;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w * 1.5), (int)(h * 1.5), 144, 144, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x7A, 0x9C, 0xC6)), null, new Rect(0, 0, w, h));
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
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        encoder.Save(fs);
    }
}
