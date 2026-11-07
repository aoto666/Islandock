using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using static Islandock.NativeMethods;

namespace Islandock;

/// <summary>
/// Mac 風 Dock 本体。
/// 画面下の横幅いっぱいに「透明なウィンドウ」を置き、その中央に Dock を描画している。
/// </summary>
public partial class DockWindow : Window
{
    // ===== 見た目の設定（好みで数値を変えてみてください） =====
    private const double BaseSize = 48;      // 通常時のアイコンサイズ
    private const double MaxSize = 84;       // マウスを乗せたときの最大サイズ
    private const double Gap = 4;            // アイコン同士の間隔
    private const double Range = 165;        // 拡大の影響が届く範囲(px)
    private const double BgPadding = 6;      // 背景の左右の余白
    private const double BgHeight = BaseSize + 13;
    private const double CornerRadius = 26;  // 背景の角丸（最大は BgHeight の半分 ≒ 30 で、両端が完全な半円になる）
    private const double SeparatorSlot = 17; // 区切り線が占める幅
    private const double Smoothness = 0.28;  // 拡大アニメの滑らかさ(0〜1, 大きいほど速い)

    // ===== 自動で隠す =====
    private const double RevealEdge = 3;     // 画面の下端からこの距離(px)以内にマウスが来たら表示
    private const double HideOffset = BgHeight + 30; // 隠すときに下へずらす量（影まで隠れるように）
    private const double SlideSpeed = 0.22;  // 出し入れアニメの速さ(0〜1)
    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(400); // 離れてから隠すまで

    private const string RecycleBinPath = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    private readonly List<DockEntry> _entries;
    private readonly DockSettings _settings;
    private readonly List<DockItem> _items = new();
    private readonly DispatcherTimer _runningTimer;
    private readonly DispatcherTimer _desktopTimer;
    private DockTheme _theme = DockTheme.Current();
    private Rectangle? _separator;
    private DockItem? _trash;
    private double _mouseX = double.NaN; // マウスが Dock 上にないときは NaN
    private IntPtr _hwnd;
    private bool _dockShown;
    private DateTime _lastNeeded = DateTime.MinValue;

    public DockWindow()
    {
        InitializeComponent();

        DockBackground.Height = BgHeight;
        DockBackground.CornerRadius = new CornerRadius(CornerRadius);
        DockInner.CornerRadius = new CornerRadius(CornerRadius - 0.5);
        PlaceWindow();

        _settings = DockConfig.LoadSettings();
        _entries = DockConfig.Load();
        SyncDesktopApps(rebuild: false);
        RebuildItems();
        ApplyTheme();

        // 起動直後から隠しておく
        _dockShown = !_settings.AutoHide;
        DockSlide.Y = _dockShown ? 0 : HideOffset;

        // 10秒ごとにデスクトップを確認し、新しく置かれたアプリを Dock に追加
        _desktopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _desktopTimer.Tick += (_, _) => SyncDesktopApps(rebuild: true);
        _desktopTimer.Start();

        // 毎フレーム呼ばれる処理（拡大アニメーション）
        CompositionTarget.Rendering += OnRendering;

        // 2秒ごとに「起動中のアプリ」を調べて点を表示
        _runningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _runningTimer.Tick += (_, _) => UpdateRunningIndicators();
        _runningTimer.Start();

        // アプリのアイコンにカーソルを乗せたら、そのアプリのウィンドウの一覧（縮小画像）を出す
        _previewTimer.Tick += (_, _) => UpdateWindowPreview();
        _previewTimer.Start();

        MouseMove += (_, e) => _mouseX = e.GetPosition(Root).X;
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += OnDragRelease;
        _longPress.Tick += (_, _) =>
        {
            _longPress.Stop();
            if (_pressItem != null && System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
                BeginDrag(_pressItem);
        };
        MouseLeave += (_, _) => _mouseX = double.NaN;
        DragOver += OnDragOver;
        Drop += OnDrop;
        DockBackground.MouseRightButtonUp += (_, e) => { ShowDockMenu(); e.Handled = true; };

        // タスクバーの設定変更で位置を直す / ライト・ダーク切替に追従する
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        App.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>設定画面で何かが変わったら、見た目と並びに反映する</summary>
    private void OnSettingsChanged()
    {
        ApplyTheme();
        if (_settings.ShowTrash != (_trash != null)) RebuildItems();
        if (_settings.AutoAddDesktopApps) SyncDesktopApps(rebuild: true);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Alt+Tab の一覧に Dock が出ないようにする
        _hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

        // タイトルバーの最小化ボタンでしまうときも、Dock のアイコンへ吸い込ませる
        Genie.WatchTitleBarMinimize(
            hwnd => _settings.OpenAnimation && FindDockItem(hwnd) != null,
            RevealIconFor,
            () => DpiScale);
    }

    /// <summary>そのウィンドウを持つ Dock のアプリ（なければ null）</summary>
    private DockItem? FindDockItem(IntPtr hwnd)
    {
        var apps = AppItems.ToList();
        var launched = apps.FirstOrDefault(i => i.Entry.LaunchedWindow == hwnd);
        if (launched != null) return launched;

        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return apps.FirstOrDefault(i => System.IO.Path.GetFileNameWithoutExtension(i.TargetPath)
                .Equals(p.ProcessName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private DateTime _forceShowUntil = DateTime.MinValue;

    /// <summary>Dock を（隠れていれば）出して、アプリのアイコンの「出たときの」位置を返す</summary>
    private Rect RevealIconFor(IntPtr hwnd)
    {
        var item = FindDockItem(hwnd);
        if (item == null) return Rect.Empty;
        _forceShowUntil = DateTime.Now + TimeSpan.FromMilliseconds(1100);
        _dockShown = true;
        var r = IconRectPx(item);
        if (!r.IsEmpty) r.Offset(0, -DockSlide.Y * DpiScale); // せり上がってくる途中でも、着地点を狙う
        return r;
    }

    protected override void OnClosed(EventArgs e)
    {
        Genie.StopWatchingTitleBarMinimize();
        CompositionTarget.Rendering -= OnRendering;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        App.SettingsChanged -= OnSettingsChanged;
        _runningTimer.Stop();
        _desktopTimer.Stop();
        _previewTimer.Stop();
        _preview?.Close();
        base.OnClosed(e);
    }

    // ===== 配置・テーマ =====

    private void PlaceWindow()
    {
        var area = SystemParameters.WorkArea; // タスクバーを除いた画面領域
        Width = area.Width;
        Height = MaxSize + 80; // 拡大したアイコンと名前ラベルが入る高さ
        Left = area.Left;
        Top = area.Bottom - Height;
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
            Dispatcher.Invoke(PlaceWindow);
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General)
            Dispatcher.Invoke(ApplyTheme);
    }

    private void ApplyTheme()
    {
        _theme = DockTheme.Current();
        _theme.ApplyMenuResources();

        DockBackground.BorderBrush = _theme.OuterBorder;
        DockInner.BorderBrush = _theme.InnerHighlight;
        DockInner.Background = _theme.BackgroundFor(_settings.DockTransparency);

        NameLabel.Background = _theme.LabelBackground;
        NameLabel.BorderBrush = _theme.LabelBorder;
        NameText.Foreground = _theme.LabelForeground;

        if (_separator != null) _separator.Fill = _theme.Separator;
        foreach (var item in _items) item.ApplyStyle(_theme, _settings.IconTiles);
    }

    private void RebuildItems()
    {
        IconPanel.Children.Clear();
        _items.Clear();

        foreach (var entry in _entries)
            AddItemVisual(new DockItem(entry, BaseSize, Gap));

        // アプリの並びの最後に「＋」ボタン（押すとアプリ一覧から追加できる）
        AddItemVisual(new DockItem(new DockEntry { Name = "アプリを追加" }, BaseSize, Gap, isAddButton: true));

        _separator = null;
        _trash = null;

        // 区切り線とごみ箱（macOS の Dock の右端と同じ構成）。設定で非表示にできる
        if (!_settings.ShowTrash)
        {
            foreach (var item in _items) item.ApplyStyle(_theme, _settings.IconTiles);
            UpdateRunningIndicators();
            return;
        }

        _separator = new Rectangle
        {
            Width = 1,
            Height = BaseSize * 0.78,
            Margin = new Thickness((SeparatorSlot - 1) / 2, 0, (SeparatorSlot - 1) / 2, 6 + BaseSize * 0.11),
            VerticalAlignment = VerticalAlignment.Bottom,
            Fill = _theme.Separator,
        };
        IconPanel.Children.Add(_separator);

        _trash = new DockItem(new DockEntry { Name = "ごみ箱", Path = RecycleBinPath }, BaseSize, Gap, isTrash: true);
        AddItemVisual(_trash);

        foreach (var item in _items) item.ApplyStyle(_theme, _settings.IconTiles);
        UpdateRunningIndicators();
    }

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    // ===== アイコンに乗せたときのウィンドウ一覧（Windows のタスクバーと同じ） =====

    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private WindowPreview? _preview;
    private DockItem? _hoverItem;
    private DateTime _hoverSince;
    private DateTime _previewLeftAt = DateTime.MaxValue;
    private bool _previewSuppressed; // アイコンを押した・右クリックした後は、いったん離れるまで出さない
    private static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan PreviewHideDelay = TimeSpan.FromMilliseconds(300);

    private void UpdateWindowPreview()
    {
        bool overPreview = _preview?.IsMouseOver == true;
        var hovered = _dragItem == null && !ContextMenuIsOpen && _dockShown
            ? AppItems.FirstOrDefault(i => i.Visual.IsMouseOver)
            : null;

        if (hovered != _hoverItem)
        {
            _hoverItem = hovered;
            _hoverSince = DateTime.Now;
            _previewSuppressed = false;
        }

        if (hovered != null)
        {
            _previewLeftAt = DateTime.MaxValue;
            if (_preview?.Item == hovered || _previewSuppressed) return;
            // 一覧を出している間に隣のアイコンへ移ったら、待たずに切り替える
            if (_preview == null && DateTime.Now - _hoverSince < PreviewDelay) return;
            ShowWindowPreview(hovered);
            return;
        }

        if (_preview == null) return;
        if (overPreview)
        {
            _previewLeftAt = DateTime.MaxValue;
            return;
        }
        if (_previewLeftAt == DateTime.MaxValue) _previewLeftAt = DateTime.Now;
        else if (DateTime.Now - _previewLeftAt > PreviewHideDelay) HideWindowPreview();
    }

    private void ShowWindowPreview(DockItem item)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(item.TargetPath);
        var windows = name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            ? new List<IntPtr>()
            : FindAppWindows(item, name).Where(h => !WindowPreview.IsCloaked(h)).ToList();

        HideWindowPreview();
        if (windows.Count == 0) return;

        _preview = new WindowPreview(item, windows, _theme, h => ActivateFromPreview(item, h));
        _preview.Closed += (s, _) => { if (_preview == s) _preview = null; };
        var top = item.Visual.PointToScreen(new Point(item.Visual.ActualWidth / 2, 0));
        _preview.ShowAbove(new Point(top.X / DpiScale, top.Y / DpiScale));
    }

    private void HideWindowPreview()
    {
        _previewLeftAt = DateTime.MaxValue;
        var p = _preview;
        _preview = null;
        p?.Close();
    }

    /// <summary>一覧で押されたウィンドウを前に出す（しまってあれば、アイコンから引き伸ばして戻す）</summary>
    private void ActivateFromPreview(DockItem item, IntPtr hwnd)
    {
        HideWindowPreview();
        if (!IsWindow(hwnd)) return;
        if (IsIconic(hwnd))
        {
            if (_settings.OpenAnimation)
            {
                Genie.Open(hwnd, IconRectPx(item), DpiScale);
                return;
            }
            ShowWindow(hwnd, SW_RESTORE);
        }
        SetForegroundWindow(hwnd);
    }

    private void AddItemVisual(DockItem item)
    {
        item.Visual.PreviewMouseLeftButtonDown += (_, e) => OnItemPress(item, e);
        item.Visual.MouseLeftButtonUp += (_, _) =>
        {
            if (DateTime.Now < _suppressClickUntil) return; // つまんで動かした直後はクリック扱いにしない
            Launch(item);
        };
        item.Visual.MouseRightButtonUp += (_, e) => { ShowItemMenu(item); e.Handled = true; };
        _items.Add(item);
        IconPanel.Children.Add(item.Visual);
    }

    // ===== 毎フレームの処理 =====

    private void OnRendering(object? sender, EventArgs e)
    {
        TrackForeground();
        UpdateAutoHide();
        AnimateMagnification();
        DockBackground.Width = Math.Max(IconPanel.ActualWidth + BgPadding * 2, 80);
        UpdateNameLabel();
    }

    /// <summary>
    /// マウスが画面の下端に来たら Dock を出し、離れたら少し待ってから隠す。
    /// 隠れている間はウィンドウにマウスイベントが来ないので、カーソル位置を直接調べる。
    /// </summary>
    private void UpdateAutoHide()
    {
        bool needed = !_settings.AutoHide || DateTime.Now < _forceShowUntil || ContextMenuIsOpen || _dragItem != null ||
                      IsHomeScreen() || !IsFrontMaximized() || IsDockNeededByCursor() ||
                      _preview?.IsMouseOver == true;
        var now = DateTime.Now;
        if (needed)
        {
            _lastNeeded = now;
            _dockShown = true;
        }
        else if (_dockShown && now - _lastNeeded > HideDelay)
        {
            _dockShown = false;
        }

        double target = _dockShown ? 0 : HideOffset;
        double diff = target - DockSlide.Y;
        DockSlide.Y = Math.Abs(diff) < 0.3 ? target : DockSlide.Y + diff * SlideSpeed;

        // 隠れている間は拡大させない
        if (!_dockShown) _mouseX = double.NaN;
    }

    private DateTime _homeCheckedAt;
    private bool _isHome;

    /// <summary>
    /// ホーム画面（デスクトップが前面）か。アプリを全部最小化した・デスクトップをクリックしたときなど。
    /// このときは自動で隠さず、Dock を出したままにする。
    /// </summary>
    private bool IsHomeScreen()
    {
        var now = DateTime.Now;
        if (now - _homeCheckedAt < TimeSpan.FromMilliseconds(150)) return _isHome;
        _homeCheckedAt = now;

        // Dock やアイランドを触っている間は、その前に使っていたウィンドウで判断する
        IntPtr fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out uint pid);
        IntPtr target = fg != IntPtr.Zero && pid == OwnProcessId ? _lastAppForeground : fg;

        _isHome = target == IntPtr.Zero || !IsWindow(target) || IsIconic(target) || !IsWindowVisible(target) ||
                  ClassOf(target) is "Progman" or "WorkerW";
        return _isHome;
    }

    private DateTime _maxCheckedAt;
    private bool _frontMaximized;

    /// <summary>手前のアプリが最大化・全画面か。そうでなければ Dock は隠さず出したままにする。</summary>
    private bool IsFrontMaximized()
    {
        var now = DateTime.Now;
        if (now - _maxCheckedAt < TimeSpan.FromMilliseconds(150)) return _frontMaximized;
        _maxCheckedAt = now;
        _frontMaximized = IsFrontAppMaximized();
        return _frontMaximized;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    private bool IsDockNeededByCursor()
    {
        if (!GetCursorPos(out var pt) || PresentationSource.FromVisual(Root) == null) return false;
        var p = Root.PointFromScreen(new Point(pt.X, pt.Y)); // 物理座標 → このウィンドウ内の座標
        if (p.X < 0 || p.X > Root.ActualWidth) return false;

        // 画面の下端（タスクバーがあればその上の端から下すべて）に触れたら表示
        if (p.Y >= Root.ActualHeight - RevealEdge) return true;

        // 表示中は、Dock（拡大したアイコンの高さまで）の上にいる間は出したまま
        if (_dockShown)
        {
            double halfWidth = DockBackground.ActualWidth / 2 + 10;
            double center = Root.ActualWidth / 2;
            if (Math.Abs(p.X - center) <= halfWidth && p.Y >= Root.ActualHeight - (MaxSize + 24))
                return true;
        }
        return false;
    }

    /// <summary>Mac の Dock の「ふわっと大きくなる」動き</summary>
    private void AnimateMagnification()
    {
        double slot = BaseSize + Gap;
        double total = _items.Count * slot + (_trash != null ? SeparatorSlot : 0);
        // 拡大前のレイアウトを基準に位置を計算する（拡大で位置がずれてもガタつかない）
        double x = (Root.ActualWidth - total) / 2;
        bool magnify = _settings.Magnification && !double.IsNaN(_mouseX) && !ContextMenuIsOpen;

        foreach (var item in _items)
        {
            if (item.IsTrash) x += SeparatorSlot;
            double center = x + slot / 2;
            x += slot;

            double target = BaseSize;
            if (magnify)
            {
                double distance = Math.Abs(_mouseX - center);
                if (distance < Range)
                {
                    // 近いほど大きく（コサインカーブでなめらかに）
                    double t = (Math.Cos(distance / Range * Math.PI) + 1) / 2;
                    target = BaseSize + (MaxSize - BaseSize) * t;
                }
            }

            double diff = target - item.Size;
            item.Size = Math.Abs(diff) < 0.1 ? target : item.Size + diff * Smoothness;
        }
    }

    private void UpdateNameLabel()
    {
        var hovered = _dragItem ?? _items.FirstOrDefault(i => i.Visual.IsMouseOver);
        if (hovered == null || ContextMenuIsOpen || (_preview != null && _dragItem == null))
        {
            NameLabel.Visibility = Visibility.Collapsed;
            return;
        }

        NameText.Text = _dragItem != null && _dragRemove ? "Dock から削除" : hovered.Entry.Name;
        NameLabel.Visibility = Visibility.Visible;
        var top = hovered.Visual.TranslatePoint(new Point(hovered.Visual.ActualWidth / 2, 0), Root);
        Canvas.SetLeft(NameLabel, Math.Round(top.X - NameLabel.ActualWidth / 2));
        Canvas.SetTop(NameLabel, Math.Round(top.Y - NameLabel.ActualHeight - 10));
    }

    // ===== アプリの起動 =====

    private void Launch(DockItem item)
    {
        HideWindowPreview();
        _previewSuppressed = true;

        if (item.IsTrash)
        {
            Process.Start("explorer.exe", "shell:RecycleBinFolder");
            return;
        }
        if (item.IsAddButton)
        {
            OpenAppPicker();
            return;
        }

        // すでに起動中ならそのウィンドウを前面に出す
        if (TryActivateRunningApp(item)) return;

        try
        {
            // 起動前に「いま前面にあるウィンドウ」と「同名プロセスのウィンドウ」を覚えておく
            IntPtr foregroundBefore = GetForegroundWindow();
            string processName = System.IO.Path.GetFileNameWithoutExtension(item.TargetPath);
            var windowsBefore = GetMainWindows(processName);
            var allWindowsBefore = GetVisibleTopWindows();
            item.Entry.LaunchedWindow = IntPtr.Zero;

            // 新しいウィンドウが出た瞬間に、アイコンから引き伸ばすアニメーションを始める
            if (_settings.OpenAnimation)
            {
                var icon = IconRectPx(item);
                double dpi = DpiScale;
                Genie.WatchForNewWindow(processName, windowsBefore, hwnd => Genie.Open(hwnd, icon, dpi), TimeSpan.FromSeconds(15));
            }

            if (item.Entry.IsShellApp)
                Process.Start("explorer.exe", item.Entry.Path); // ストアアプリなどは explorer 経由で起動
            else
                Process.Start(new ProcessStartInfo(item.Entry.Path) { UseShellExecute = true });

            // 新しいウィンドウが前面に出るか、そのアプリのウィンドウが増えたら「起動完了」
            // そのとき出てきた新しいウィンドウを覚えておき、もう一度押されたらそれをしまう
            void Remember(IntPtr h)
            {
                item.Entry.LaunchedWindow = h;
                LearnProcessName(item, h);
            }

            bool IsReady()
            {
                IntPtr fg = GetForegroundWindow();
                if (fg != IntPtr.Zero && fg != foregroundBefore && fg != _hwnd)
                {
                    GetWindowThreadProcessId(fg, out uint pid);
                    if (pid != OwnProcessId && !allWindowsBefore.Contains(fg)) Remember(fg);
                    return true;
                }

                var created = GetAppWindows(processName).FirstOrDefault(h => !windowsBefore.Contains(h));
                if (created == IntPtr.Zero && processName.Length == 0)
                {
                    // プロセス名が分からないアプリ（Chrome をストアアプリ形式で登録した場合など）は、
                    // 前面に出てこなくても「新しく現れたアプリのウィンドウ」を起動したものとみなす
                    created = GetVisibleTopWindows().FirstOrDefault(h =>
                    {
                        if (allWindowsBefore.Contains(h) || !IsLikelyAppWindow(h)) return false;
                        GetWindowThreadProcessId(h, out uint pid);
                        return pid != OwnProcessId;
                    });
                }
                if (created == IntPtr.Zero) return false;
                Remember(created);
                return true;
            }
            item.BounceUntil(IsReady, TimeSpan.FromSeconds(12));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"起動できませんでした:\n{item.Entry.Path}\n\n{ex.Message}", "Islandock",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// そのプロセス名のアプリが持つウィンドウを、手前にあるものから順にすべて返す。
    /// （Chrome のように1つのプロセスが複数のウィンドウを持つアプリにも対応）
    /// </summary>
    private static List<IntPtr> GetAppWindows(string processName)
    {
        var pids = new HashSet<uint>();
        if (string.IsNullOrEmpty(processName)) return new List<IntPtr>();
        foreach (var p in Process.GetProcessesByName(processName))
        {
            using (p) pids.Add((uint)p.Id);
        }
        return GetAppWindowsOf(pids);
    }

    private static HashSet<IntPtr> GetMainWindows(string processName) => new(GetAppWindows(processName));

    // 覚えても意味がない（いろいろなアプリのウィンドウを代わりに出している）プロセス
    private static readonly HashSet<string> SharedHostProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ApplicationFrameHost", "Islandock",
    };

    /// <summary>
    /// ストアアプリ形式（shell:…）で登録したアプリは、パスからプロセス名が分からない。
    /// Dock から起動して出てきたウィンドウのプロセス名を覚えておき、次からはそれで探せるようにする。
    /// </summary>
    private void LearnProcessName(DockItem item, IntPtr hwnd)
    {
        if (!item.Entry.IsShellApp || !string.IsNullOrEmpty(item.Entry.ProcessName)) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (SharedHostProcesses.Contains(p.ProcessName)) return;
            item.Entry.ProcessName = p.ProcessName;
            DockConfig.Save(_entries);
        }
        catch
        {
        }
    }

    private bool TryActivateRunningApp(DockItem item)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(item.TargetPath);
        // エクスプローラーは常に動いているので、毎回新しいウィンドウを開く
        if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return false;

        var windows = FindAppWindows(item, name);
        if (windows.Count == 0) return false;

        // いちばん手前に出ているアプリをもう一度押したら、開くときの逆再生で Dock へしまう
        IntPtr fg = GetForegroundWindow();
        IntPtr front = windows.FirstOrDefault(h => !IsIconic(h) && (h == _lastAppForeground || h == fg));
        if (front != IntPtr.Zero)
        {
            if (_settings.OpenAnimation) Genie.Minimize(front, IconRectPx(item), DpiScale);
            else ShowWindow(front, SW_MINIMIZE);
            return true;
        }

        // macOS と同じく、しまっていないウィンドウがあれば、それを全部（重なり順はそのまま）手前に出す
        var open = windows.Where(h => !IsIconic(h)).ToList();
        if (open.Count > 0)
        {
            for (int i = open.Count - 1; i >= 1; i--)
                SetWindowPos(open[i], HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            SetForegroundWindow(open[0]);
            return true;
        }

        // 全部しまってあるときは、最後にしまったウィンドウを 1 つだけ戻す（ほかは右側のアイコンから戻せる）
        IntPtr hwnd = windows[0];
        // 最小化されていたら、アイコンから引き伸ばしながら戻す（前面に出すのも Genie 側で行う）
        if (_settings.OpenAnimation)
        {
            Genie.Open(hwnd, IconRectPx(item), DpiScale);
            return true;
        }
        ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
        return true;
    }

    /// <summary>
    /// そのアプリのウィンドウを集める。
    /// Dock から起動したときに覚えたウィンドウを優先し、続いて同じ名前のプロセスのウィンドウ。
    /// </summary>
    private static List<IntPtr> FindAppWindows(DockItem item, string processName)
    {
        var list = new List<IntPtr>();
        IntPtr launched = item.Entry.LaunchedWindow;
        if (launched != IntPtr.Zero)
        {
            if (IsWindow(launched) && IsWindowVisible(launched)) list.Add(launched);
            else item.Entry.LaunchedWindow = IntPtr.Zero; // 閉じられていたら忘れる
        }

        if (processName.Length > 0)
        {
            foreach (var h in GetMainWindows(processName))
                if (!list.Contains(h)) list.Add(h);
        }
        return list;
    }

    /// <summary>
    /// Dock を押すと Dock 自身が前面になってしまうので、
    /// その直前に前面だった「他のアプリのウィンドウ」を毎フレーム覚えておく。
    /// </summary>
    private IntPtr _lastAppForeground;
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    private void TrackForeground()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        GetWindowThreadProcessId(fg, out uint pid);
        if (pid != OwnProcessId) _lastAppForeground = fg;
    }

    private double DpiScale =>VisualTreeHelper.GetDpi(this).DpiScaleX;

    /// <summary>アイコンの画面上の位置（物理ピクセル）</summary>
    private static Rect IconRectPx(DockItem item)
    {
        var v = item.Visual;
        try
        {
            return new Rect(v.PointToScreen(new Point(0, 0)), v.PointToScreen(new Point(v.ActualWidth, v.ActualHeight)));
        }
        catch
        {
            return Rect.Empty;
        }
    }

    // ===== 長押しでつまんで並べ替え =====

    private readonly DispatcherTimer _longPress = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private DockItem? _pressItem;
    private Point _pressPoint;
    private DockItem? _dragItem;
    private bool _dragRemove;
    private DateTime _suppressClickUntil = DateTime.MinValue;

    private void OnItemPress(DockItem item, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (item.IsTrash || item.IsAddButton || _dragItem != null) return;
        _pressItem = item;
        _pressPoint = e.GetPosition(Root);
        _longPress.Start();
    }

    private void BeginDrag(DockItem item)
    {
        if (item.IsBouncing) return;
        HideWindowPreview();
        _previewSuppressed = true;
        _longPress.Stop();
        _pressItem = null;
        _dragItem = item;
        _dragRemove = false;
        item.SetLifted(true);
        item.Visual.CaptureMouse();
    }

    private IEnumerable<DockItem> AppItems => _items.Where(i => !i.IsTrash && !i.IsAddButton);

    /// <summary>ずれを除いた、本来の位置の中心 X</summary>
    private double HomeCenter(DockItem item) =>
        item.Visual.TranslatePoint(new Point(item.Visual.ActualWidth / 2, 0), Root).X - item.Offset.X;

    private void OnDragMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var pos = e.GetPosition(Root);

        // 長押しを待たずに大きく動かしたら、その場でつまむ
        if (_pressItem != null && _dragItem == null && (pos - _pressPoint).Length > 8)
            BeginDrag(_pressItem);
        if (_dragItem == null) return;

        _mouseX = pos.X;
        var apps = AppItems.ToList();
        int index = apps.IndexOf(_dragItem);

        // 隣のアイコンの中心を越えたら入れ替える（越えた側のアイコンはすっと滑って場所を空ける）
        if (index > 0 && pos.X < HomeCenter(apps[index - 1])) MoveItem(index, index - 1);
        else if (index < apps.Count - 1 && pos.X > HomeCenter(apps[index + 1])) MoveItem(index, index + 1);

        // Dock から上に大きく離したら「削除」
        _dragRemove = pos.Y < Root.ActualHeight - (MaxSize + 70);
        _dragItem.Visual.Opacity = _dragRemove ? 0.45 : 0.88;

        // つまんだアイコンはカーソルについてくる
        _dragItem.SetOffset(pos.X - HomeCenter(_dragItem), Math.Min(0, pos.Y - _pressPoint.Y));
    }

    private void MoveItem(int from, int to)
    {
        var neighbor = AppItems.ElementAt(to);
        double before = HomeCenter(neighbor);

        // つまんでいるアイコンを外すとマウスの捕捉が切れるので、隣のほうを動かす
        _items.Remove(neighbor);
        _items.Insert(from, neighbor);
        IconPanel.Children.Remove(neighbor.Visual);
        IconPanel.Children.Insert(from, neighbor.Visual);
        IconPanel.UpdateLayout();

        // 押しのけられたアイコンを、元いた場所から新しい場所へ滑らせる
        double after = HomeCenter(neighbor);
        neighbor.SetOffset(before - after, 0);
        neighbor.SlideHome();
    }

    private void OnDragRelease(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _longPress.Stop();
        _pressItem = null;
        if (_dragItem == null) return;

        var item = _dragItem;
        _dragItem = null;
        item.Visual.ReleaseMouseCapture();
        _suppressClickUntil = DateTime.Now.AddMilliseconds(300);

        if (_dragRemove)
        {
            _entries.Remove(item.Entry);
            SaveAndRebuild();
            return;
        }

        item.SetLifted(false);
        item.SlideHome();

        // 新しい並びを保存
        var order = AppItems.Select(i => i.Entry).ToList();
        _entries.Clear();
        _entries.AddRange(order);
        DockConfig.Save(_entries);
    }

    private void UpdateRunningIndicators()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p) running.Add(p.ProcessName);
        }
        foreach (var item in _items)
        {
            if (item.IsTrash || item.IsAddButton) continue;
            string name = System.IO.Path.GetFileNameWithoutExtension(item.TargetPath);
            IntPtr launched = item.Entry.LaunchedWindow;
            bool launchedAlive = launched != IntPtr.Zero && IsWindow(launched);
            item.IsRunning = (name.Length > 0 && running.Contains(name)) || launchedAlive;
        }
    }

    // ===== 右クリックメニュー =====

    private bool ContextMenuIsOpen => DockBackground.ContextMenu?.IsOpen == true
                                      || _items.Any(i => i.Visual.ContextMenu?.IsOpen == true);

    private static MenuItem Item(string header, Action onClick, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void ShowItemMenu(DockItem item)
    {
        HideWindowPreview();
        _previewSuppressed = true;

        var menu = new ContextMenu();

        if (item.IsAddButton)
        {
            ShowDockMenu();
            return;
        }

        if (item.IsTrash)
        {
            menu.Items.Add(Item("開く", () => Launch(item)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("ごみ箱を空にする...", () => SHEmptyRecycleBin(IntPtr.Zero, null, 0)));
        }
        else
        {
            int index = _entries.IndexOf(item.Entry);
            menu.Items.Add(Item("開く", () => Launch(item)));
            menu.Items.Add(Item("エクスプローラーで表示", () =>
                Process.Start("explorer.exe", $"/select,\"{item.Entry.Path}\""), enabled: !item.Entry.IsShellApp));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("左へ移動", () => MoveEntry(item.Entry, -1), index > 0));
            menu.Items.Add(Item("右へ移動", () => MoveEntry(item.Entry, +1), index < _entries.Count - 1));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Dock から削除", () =>
            {
                _entries.Remove(item.Entry);
                SaveAndRebuild();
            }));
        }

        item.Visual.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private void ShowDockMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(Item("Islandock の設定...", SettingsWindow.Open));
        menu.Items.Add(new Separator());

        var magnify = new MenuItem { Header = "拡大", IsCheckable = true, IsChecked = _settings.Magnification };
        magnify.Click += (_, _) =>
        {
            _settings.Magnification = magnify.IsChecked;
            DockConfig.SaveSettings(_settings);
        };
        menu.Items.Add(magnify);

        var autoHide = new MenuItem { Header = "Dock を自動的に表示/非表示", IsCheckable = true, IsChecked = _settings.AutoHide };
        autoHide.Click += (_, _) =>
        {
            _settings.AutoHide = autoHide.IsChecked;
            DockConfig.SaveSettings(_settings);
        };
        menu.Items.Add(autoHide);

        var genie = new MenuItem { Header = "開く/しまうときのアニメーション（ジニー）", IsCheckable = true, IsChecked = _settings.OpenAnimation };
        genie.Click += (_, _) =>
        {
            _settings.OpenAnimation = genie.IsChecked;
            DockConfig.SaveSettings(_settings);
        };
        menu.Items.Add(genie);

        var tiles = new MenuItem { Header = "アイコンをタイルに揃える", IsCheckable = true, IsChecked = _settings.IconTiles };
        tiles.Click += (_, _) =>
        {
            _settings.IconTiles = tiles.IsChecked;
            DockConfig.SaveSettings(_settings);
            foreach (var i in _items) i.ApplyStyle(_theme, _settings.IconTiles);
        };
        menu.Items.Add(tiles);

        var autoAdd = new MenuItem { Header = "デスクトップのアプリを自動で追加", IsCheckable = true, IsChecked = _settings.AutoAddDesktopApps };
        autoAdd.Click += (_, _) =>
        {
            _settings.AutoAddDesktopApps = autoAdd.IsChecked;
            DockConfig.SaveSettings(_settings);
            if (autoAdd.IsChecked) SyncDesktopApps(rebuild: true);
        };
        menu.Items.Add(autoAdd);

        var trash = new MenuItem { Header = "ごみ箱を表示", IsCheckable = true, IsChecked = _settings.ShowTrash };
        trash.Click += (_, _) =>
        {
            _settings.ShowTrash = trash.IsChecked;
            DockConfig.SaveSettings(_settings);
            RebuildItems();
        };
        menu.Items.Add(trash);

        var clock = new MenuItem { Header = "時計ウィジェットを表示", IsCheckable = true, IsChecked = ClockWidget.IsShown };
        clock.Click += (_, _) =>
        {
            if (clock.IsChecked) ClockWidget.ShowWidget();
            else ClockWidget.HideWidget();
        };
        menu.Items.Add(clock);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("アプリを追加...", OpenAppPicker));
        menu.Items.Add(Item("ファイルを選んで追加...", AddByDialog));
        menu.Items.Add(Item("設定ファイルを開く", () =>
            Process.Start(new ProcessStartInfo(DockConfig.ConfigPath) { UseShellExecute = true })));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Islandock を終了", () => Application.Current.Shutdown()));

        DockBackground.ContextMenu = menu;
        menu.IsOpen = true;
    }

    // ===== アプリの追加・並べ替え・ごみ箱へのドロップ =====

    /// <summary>
    /// デスクトップにある「まだ見たことのない」アプリを Dock に追加する。
    /// 一度見たものは記録するので、Dock から削除したアプリが勝手に戻ってくることはない。
    /// </summary>
    private void SyncDesktopApps(bool rebuild)
    {
        if (!_settings.AutoAddDesktopApps) return;

        var seen = new HashSet<string>(_settings.SeenDesktopItems, StringComparer.OrdinalIgnoreCase);
        var newFiles = DesktopApps.Find().Where(f => !seen.Contains(f)).ToList();
        if (newFiles.Count == 0) return;

        foreach (var file in newFiles)
        {
            _settings.SeenDesktopItems.Add(file);
            if (_entries.Any(x => x.Path.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
            _entries.Add(new DockEntry { Name = DesktopApps.DisplayName(file), Path = file });
        }

        DockConfig.SaveSettings(_settings);
        DockConfig.Save(_entries);
        if (rebuild) RebuildItems();
    }

    /// <summary>PC に入っているアプリの一覧から選んで追加する</summary>
    private void OpenAppPicker()
    {
        var picker = new AppPickerWindow(_entries);
        if (picker.ShowDialog() != true || picker.SelectedEntries.Count == 0) return;
        _entries.AddRange(picker.SelectedEntries);
        SaveAndRebuild();
    }

    private void AddByDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Dock に追加するアプリを選択",
            Filter = "アプリ・ショートカット (*.exe;*.lnk)|*.exe;*.lnk|すべてのファイル (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Multiselect = true,
        };
        if (dialog.ShowDialog() == true) AddFiles(dialog.FileNames);
    }

    private bool IsOverTrash(DragEventArgs e)
    {
        if (_trash == null) return false;
        var p = e.GetPosition(_trash.Visual);
        return p.X >= 0 && p.Y >= 0 && p.X <= _trash.Visual.ActualWidth && p.Y <= _trash.Visual.ActualHeight;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        _mouseX = e.GetPosition(Root).X; // ドラッグ中も拡大させる
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.None;
        else
            e.Effects = IsOverTrash(e) ? DragDropEffects.Move : DragDropEffects.Link;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        if (IsOverTrash(e))
            MoveToRecycleBin(files);
        else
            AddFiles(files);
    }

    private static void MoveToRecycleBin(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            try
            {
                if (System.IO.Directory.Exists(file))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(file,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                else
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ごみ箱に移動できませんでした:\n{file}\n\n{ex.Message}", "Islandock",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void AddFiles(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            if (_entries.Any(x => x.Path.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
            _entries.Add(new DockEntry
            {
                Name = DesktopApps.DisplayName(file),
                Path = file,
            });
        }
        SaveAndRebuild();
    }

    private void MoveEntry(DockEntry entry, int delta)
    {
        int index = _entries.IndexOf(entry);
        int newIndex = index + delta;
        if (index < 0 || newIndex < 0 || newIndex >= _entries.Count) return;
        _entries.RemoveAt(index);
        _entries.Insert(newIndex, entry);
        SaveAndRebuild();
    }

    private void SaveAndRebuild()
    {
        DockConfig.Save(_entries);
        RebuildItems();
    }
}
