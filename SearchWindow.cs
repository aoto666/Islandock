using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static Islandock.NativeMethods;

namespace Islandock;

/// <summary>
/// macOS の Spotlight 風の検索バー。Alt + Space で画面の中央に出て、アプリとファイルを探して開ける。
///
///  - 後ろがぼける、角の丸いガラスのパネル
///  - 入力するとすぐに「トップヒット / アプリケーション / フォルダ / ファイル」に分けて表示
///  - ↑↓ で選ぶ、Enter で開く、Ctrl+Enter でエクスプローラーで場所を表示、Esc で閉じる
///  - 計算式（例: 12*3+4）を入れると答えを出す。Enter でコピー
/// </summary>
public sealed class SearchWindow : Window
{
    private const double PanelWidth = 680;
    private const double Radius = 24;
    private const double ResultsMaxHeight = 430;
    private const int HotkeyId = 0x1D0C;

    private static SearchWindow? _instance;

    /// <summary>呼び出すキーの表示用（Alt + Space が使えなかったときは Ctrl + Alt + Space）</summary>
    public static string HotkeyText { get; private set; } = "Alt + Space";

    // ---- 外から呼ぶ用 ----

    public static void ApplySetting(bool enabled, bool pinned = false)
    {
        if (!enabled)
        {
            Stop();
            return;
        }
        Start();
        _instance!.SetPinned(pinned);
    }

    public static void Start()
    {
        if (_instance != null) return;
        _instance = new SearchWindow();
        new WindowInteropHelper(_instance).EnsureHandle(); // 表示しなくてもホットキーを受け取れるように
        SearchIndex.RefreshInBackground();
    }

    public static void Stop()
    {
        if (_instance == null) return;
        _instance._closing = true;
        _instance.Close();
        _instance = null;
    }

    /// <summary>出ていなければ出す、出ていれば隠す</summary>
    public static void Toggle()
    {
        if (_instance == null) return;
        // デスクトップに置くモードでは、いつも見えているので「前面で入力中かどうか」で切り替える
        bool open = _instance._pinned ? _instance.IsActive && _instance.Topmost : _instance.IsVisible;
        if (open) _instance.HidePanel(handOffFocus: true);
        else _instance.ShowPanel();
    }

    // ---- 本体 ----

    private readonly Border _panel = new();
    private readonly TextBox _query = new();
    private readonly TextBlock _placeholder = new();
    private readonly TextBlock _glass = new();
    private readonly Border _divider = new();
    private readonly ScrollViewer _scroll = new();
    private readonly StackPanel _list = new();
    private readonly List<(Border Row, SearchItem Item)> _rows = new();
    private int _selected = -1;
    private IntPtr _hwnd;
    private bool _closing;
    private Palette _p = Palette.Light;
    private bool _mouseMoved;
    private POINT _cursorAtShow;

    /// <summary>常にデスクトップに出しておくモード</summary>
    private bool _pinned;

    private SearchWindow()
    {
        Title = Loc.T("Islandock 検索");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = PanelWidth;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;

        // ---- 検索欄 ----
        _glass.Text = ""; // 虫めがね
        _glass.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        _glass.FontSize = 20;
        _glass.VerticalAlignment = VerticalAlignment.Center;
        _glass.Margin = new Thickness(20, 0, 12, 0);

        _query.FontSize = 22;
        _query.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, Yu Gothic UI");
        _query.Background = Brushes.Transparent;
        _query.BorderThickness = new Thickness(0);
        _query.VerticalContentAlignment = VerticalAlignment.Center;
        _query.Padding = new Thickness(0);
        _query.TextChanged += (_, _) => UpdateResults();

        _placeholder.Text = Loc.T("検索");
        _placeholder.FontSize = 22;
        _placeholder.FontFamily = _query.FontFamily;
        _placeholder.IsHitTestVisible = false;
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.Margin = new Thickness(2, 0, 0, 0);

        var field = new Grid { Height = 56, Margin = new Thickness(0, 0, 18, 0) };
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.ColumnDefinitions.Add(new ColumnDefinition());
        field.Children.Add(_glass);
        var input = new Grid { Children = { _placeholder, _query } };
        Grid.SetColumn(input, 1);
        field.Children.Add(input);

        // ---- 結果 ----
        _divider.Height = 1;
        _divider.Visibility = Visibility.Collapsed;
        _scroll.Content = _list;
        _scroll.MaxHeight = ResultsMaxHeight;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroll.Visibility = Visibility.Collapsed;
        _list.Margin = new Thickness(0, 4, 0, 8);

        var stack = new StackPanel();
        stack.Children.Add(field);
        stack.Children.Add(_divider);
        stack.Children.Add(_scroll);

        _panel.CornerRadius = new CornerRadius(Radius);
        _panel.BorderThickness = new Thickness(1);
        _panel.Child = stack;
        _panel.RenderTransformOrigin = new Point(0.5, 0);
        _panel.RenderTransform = new ScaleTransform(1, 1);
        Content = _panel;

        PreviewKeyDown += OnKey;
        PreviewMouseMove += (_, _) =>
        {
            if (_mouseMoved || !GetCursorPos(out var pt)) return;
            if (pt.X != _cursorAtShow.X || pt.Y != _cursorAtShow.Y) _mouseMoved = true;
        };
        Deactivated += (_, _) => { if (IsVisible) HidePanel(); };
        Activated += (_, _) => { if (_pinned) BringToFront(); }; // デスクトップの検索欄がクリックされた
        SizeChanged += (_, _) => ClipToRoundedShape();
        Closing += (_, e) =>
        {
            // ふつうに閉じられても消さずに隠すだけ（ホットキーを生かしておく）
            if (_closing) return;
            e.Cancel = true;
            HidePanel();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW); // Alt+Tab に出さない
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        // すりガラスは角丸の切り抜きを無視して四角くはみ出すので使わない

        RegisterBestHotkey();

        // Alt+Space 以外になっていたら、Alt+Space が空くのを待って切り替える
        // （Copilot アプリなどのショートカットをオフにすると、Mac の ⌘+Space と同じ位置で使えるようになる）
        _hotkeyRetry.Tick += (_, _) =>
        {
            if (_hotkeyIndex == 0) { _hotkeyRetry.Stop(); return; }
            RegisterBestHotkey();
        };
        if (_hotkeyIndex != 0) _hotkeyRetry.Start();
    }

    // 使いたい順（Mac の ⌘+Space と同じ位置の Alt+Space がいちばん）
    private static readonly (uint Mods, string Text)[] HotkeyChoices =
    {
        (MOD_ALT, "Alt + Space"),
        (MOD_CONTROL | MOD_ALT, "Ctrl + Alt + Space"),
        (MOD_ALT | MOD_SHIFT, "Alt + Shift + Space"),
        (MOD_CONTROL | MOD_SHIFT, "Ctrl + Shift + Space"),
    };

    private int _hotkeyIndex = -1; // いま登録できているキー（-1 = なし）
    private readonly System.Windows.Threading.DispatcherTimer _hotkeyRetry = new() { Interval = TimeSpan.FromSeconds(10) };

    /// <summary>いま登録しているものより良いキーが空いていれば、そちらに切り替える</summary>
    private void RegisterBestHotkey()
    {
        int limit = _hotkeyIndex < 0 ? HotkeyChoices.Length : _hotkeyIndex;
        for (int i = 0; i < limit; i++)
        {
            int id = HotkeyId + 1 + i;
            if (!RegisterHotKey(_hwnd, id, HotkeyChoices[i].Mods | MOD_NOREPEAT, VK_SPACE)) continue;

            if (_hotkeyIndex >= 0) UnregisterHotKey(_hwnd, HotkeyId + 1 + _hotkeyIndex);
            _hotkeyIndex = i;
            HotkeyText = HotkeyChoices[i].Text;
            Log($"hotkey: {HotkeyText}");
            return;
        }
        if (_hotkeyIndex < 0)
        {
            HotkeyText = Loc.T("（使えるキーがありません）");
            Log("hotkey: none available");
        }
    }

    /// <summary>動作の記録（%APPDATA%\Islandock\search.log）。うまく出ないときの原因調べ用</summary>
    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Islandock");
            File.AppendAllText(Path.Combine(dir, "search.log"), $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch
        {
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotkeyRetry.Stop();
        if (_hwnd != IntPtr.Zero && _hotkeyIndex >= 0) UnregisterHotKey(_hwnd, HotkeyId + 1 + _hotkeyIndex);
        base.OnClosed(e);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _hotkeyIndex >= 0 && wParam.ToInt32() == HotkeyId + 1 + _hotkeyIndex)
        {
            try
            {
                Toggle();
            }
            catch (Exception ex)
            {
                Log("toggle failed: " + ex);
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ===== 出す・隠す =====

    /// <summary>画面の中央、やや上（結果が増えると下に伸びる）</summary>
    private void PlaceWindow()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - PanelWidth) / 2;
        Top = area.Top + area.Height * 0.26;
    }

    /// <summary>常にデスクトップに出しておく / キーで呼んだときだけ出す</summary>
    private void SetPinned(bool pinned)
    {
        if (_pinned == pinned)
        {
            // 設定が変わっただけ（色・タスクバーの表示など）なら、見た目と位置だけ合わせ直す
            if (pinned && !Topmost)
            {
                ApplyTheme();
                PlaceWindow();
            }
            return;
        }
        _pinned = pinned;
        if (pinned)
        {
            ApplyTheme();
            PlaceWindow();
            _query.Clear();
            ShowActivated = false;
            Topmost = false;
            Show();
            SendToBack();
        }
        else
        {
            ShowActivated = true;
            Topmost = true;
            Hide();
        }
    }

    /// <summary>入力できるように、ほかのウィンドウより手前に出す</summary>
    private void BringToFront()
    {
        Topmost = true;
        _query.Focus();
    }

    /// <summary>デスクトップに置いた検索欄を、ほかのウィンドウの後ろに戻す</summary>
    private void SendToBack()
    {
        Topmost = false;
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    private void ShowPanel()
    {
        ApplyTheme();
        SearchIndex.RefreshInBackground(); // 古くなっていたら裏で作り直す
        PlaceWindow();

        _mouseMoved = false;
        GetCursorPos(out _cursorAtShow);
        Topmost = true;
        Show();
        Activate();
        SetForegroundWindow(_hwnd);
        _query.Focus();
        _query.SelectAll(); // Mac と同じく、前回の言葉を選んだ状態で開く
        UpdateResults();

        // ふわっと出す
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(140);
        _panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        var scale = (ScaleTransform)_panel.RenderTransform;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, duration) { EasingFunction = ease });
    }

    /// <param name="handOffFocus">デスクトップに置くモードで、前面をデスクトップに渡すか（Esc などで閉じたとき）</param>
    private void HidePanel(bool handOffFocus = false)
    {
        if (!_pinned)
        {
            Hide();
            return;
        }

        // デスクトップに置くモードでは消さずに、入力を消してカプセルに戻し、後ろへ下げる
        _query.Clear();
        SendToBack();
        if (handOffFocus && IsActive)
        {
            // 自分が前面のままだと、次にクリックしても「選ばれた」ことが分からないので、デスクトップに前面を渡す
            IntPtr desktop = GetShellWindow();
            if (desktop != IntPtr.Zero) SetForegroundWindow(desktop);
        }
    }

    // ===== 見た目 =====

    private sealed record Palette(Brush Tint, Brush Border, Brush Text, Brush SubText, Brush Header,
        Brush Divider, Brush Selected, Brush SelectedText, Brush SelectedSubText)
    {
        private static SolidColorBrush B(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        public static readonly Palette Light = new(
            Tint: B(0xEB, 0xF6, 0xF6, 0xF8), Border: B(0x2E, 0x00, 0x00, 0x00),
            Text: B(0xFF, 0x1D, 0x1D, 0x1F), SubText: B(0x8C, 0x00, 0x00, 0x00), Header: B(0x80, 0x00, 0x00, 0x00),
            Divider: B(0x1A, 0x00, 0x00, 0x00), Selected: B(0xFF, 0x0A, 0x6C, 0xF5),
            SelectedText: B(0xFF, 0xFF, 0xFF, 0xFF), SelectedSubText: B(0xC0, 0xFF, 0xFF, 0xFF));

        public static readonly Palette Dark = new(
            Tint: B(0xE6, 0x26, 0x26, 0x28), Border: B(0x40, 0xFF, 0xFF, 0xFF),
            Text: B(0xFF, 0xF5, 0xF5, 0xF7), SubText: B(0x8C, 0xFF, 0xFF, 0xFF), Header: B(0x80, 0xFF, 0xFF, 0xFF),
            Divider: B(0x1F, 0xFF, 0xFF, 0xFF), Selected: B(0xFF, 0x0A, 0x74, 0xF0),
            SelectedText: B(0xFF, 0xFF, 0xFF, 0xFF), SelectedSubText: B(0xC0, 0xFF, 0xFF, 0xFF));
    }

    private void ApplyTheme()
    {
        _p = DockTheme.IsDarkMode() ? Palette.Dark : Palette.Light;
        _panel.Background = _p.Tint;
        _panel.BorderBrush = _p.Border;
        _glass.Foreground = _p.SubText;
        _placeholder.Foreground = _p.SubText;
        _query.Foreground = _p.Text;
        _query.CaretBrush = _p.Text;
        _query.SelectionBrush = _p.Selected;
        _divider.Background = _p.Divider;
    }

    /// <summary>後ろのウィンドウをぼかす（すりガラス）</summary>
    private void EnableBlur()
    {
        var accent = new AccentPolicy { AccentState = ACCENT_ENABLE_BLURBEHIND };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = WCA_ACCENT_POLICY, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(_hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>ぼかしがパネルの角からはみ出さないよう、ウィンドウ自体を角丸に切り抜く</summary>
    private void ClipToRoundedShape()
    {
        if (_hwnd == IntPtr.Zero) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Ceiling(ActualWidth * dpi), h = (int)Math.Ceiling(ActualHeight * dpi);
        int d = (int)Math.Round(Radius * 2 * dpi);
        SetWindowRgn(_hwnd, CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d), true); // 領域は Windows が持つので解放しない
    }

    // ===== 検索結果 =====

    private void UpdateResults()
    {
        string q = _query.Text.Trim();
        _placeholder.Visibility = _query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Children.Clear();
        _rows.Clear();
        _selected = -1;

        var results = q.Length == 0 ? new SearchResults() : SearchIndex.Search(q);
        if (results.IsEmpty)
        {
            _divider.Visibility = Visibility.Collapsed;
            _scroll.Visibility = Visibility.Collapsed;
            return;
        }

        if (results.Top != null) AddSection(Loc.T("トップヒット"), new[] { results.Top }, big: true);
        AddSection(Loc.T("アプリケーション"), results.Apps);
        AddSection(Loc.T("フォルダ"), results.Folders);
        AddSection(Loc.T("ファイル"), results.Files);

        _divider.Visibility = Visibility.Visible;
        _scroll.Visibility = Visibility.Visible;
        _scroll.ScrollToTop();
        Select(0);
    }

    private void AddSection(string title, IEnumerable<SearchItem> items, bool big = false)
    {
        bool first = true;
        foreach (var item in items)
        {
            if (first)
            {
                _list.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = _p.Header,
                    Margin = new Thickness(22, _list.Children.Count == 0 ? 6 : 12, 0, 4),
                });
                first = false;
            }
            AddRow(item, big);
        }
    }

    private void AddRow(SearchItem item, bool big)
    {
        double iconSize = big ? 34 : 24;
        var icon = new Image { Width = iconSize, Height = iconSize, Source = item.Icon(), Margin = new Thickness(0, 0, 10, 0) };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

        var name = new TextBlock
        {
            Text = item.Name,
            FontSize = big ? 15 : 13.5,
            FontWeight = big ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var detail = new TextBlock
        {
            Text = item.Detail,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(14, 0, 0, 0),
            MaxWidth = 260,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        Grid.SetColumn(detail, 2);
        grid.Children.Add(detail);

        var row = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, big ? 7 : 5, 12, big ? 7 : 5),
            Margin = new Thickness(10, 0, 10, 1),
            Background = Brushes.Transparent,
            Tag = (name, detail),
        };
        int index = _rows.Count;
        // 開いた瞬間にたまたまカーソルの下にあった行は選ばない。マウスを動かしたときだけ選ぶ
        row.MouseMove += (_, _) => { if (_mouseMoved && _selected != index) Select(index); };
        row.MouseLeftButtonUp += (_, _) => Open(item, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        _rows.Add((row, item));
        _list.Children.Add(row);
    }

    private void Select(int index)
    {
        if (_rows.Count == 0) return;
        index = Math.Clamp(index, 0, _rows.Count - 1);
        for (int i = 0; i < _rows.Count; i++)
        {
            bool on = i == index;
            var (row, _) = _rows[i];
            var (name, detail) = ((TextBlock, TextBlock))row.Tag;
            row.Background = on ? _p.Selected : Brushes.Transparent;
            name.Foreground = on ? _p.SelectedText : _p.Text;
            detail.Foreground = on ? _p.SelectedSubText : _p.SubText;
        }
        _selected = index;
        _rows[index].Row.BringIntoView();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_query.Text.Length > 0) _query.Clear();
                else HidePanel(handOffFocus: true);
                e.Handled = true;
                break;
            case Key.Down:
                Select(_selected + 1);
                e.Handled = true;
                break;
            case Key.Up:
                Select(_selected - 1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (_selected >= 0 && _selected < _rows.Count)
                    Open(_rows[_selected].Item, reveal: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;
        }
    }

    private void Open(SearchItem item, bool reveal)
    {
        HidePanel();
        try
        {
            switch (item.Kind)
            {
                case SearchKind.Calc:
                    Clipboard.SetText(item.Name.TrimStart('=', ' '));
                    break;
                case SearchKind.App when item.Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase):
                    Process.Start("explorer.exe", item.Path);
                    break;
                default:
                    if (reveal && item.Kind != SearchKind.App)
                        Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
                    else
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Loc.F("開けませんでした:\n{0}\n\n{1}", item.Path, ex.Message), "Islandock", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ===== 開発用プレビュー =====

    /// <summary>Islandock.exe --preview-search (light|dark) 言葉 出力.png ： 検索結果の見た目を画像に書き出す（ぼかしは入らない）</summary>
    public static void RenderPreview(string outputPath, bool dark, string query)
    {
        SearchIndex.RefreshNow();
        var w = new SearchWindow { Left = -10000, Top = -10000, ShowActivated = false };
        w._p = dark ? Palette.Dark : Palette.Light;
        w.ApplyThemeFixed(dark);
        w.Show();
        w._query.Text = query;
        w.UpdateLayout();

        var backdrop = new Border
        {
            Padding = new Thickness(40),
            Background = new LinearGradientBrush(
                dark ? Color.FromRgb(0x1C, 0x2B, 0x4A) : Color.FromRgb(0x8E, 0xB8, 0xE8),
                dark ? Color.FromRgb(0x3A, 0x1F, 0x4D) : Color.FromRgb(0xE8, 0xB4, 0xD0), 45),
        };
        var panel = w._panel;
        w.Content = null;
        panel.Width = PanelWidth;
        backdrop.Child = panel;
        backdrop.Measure(new Size(PanelWidth + 80, double.PositiveInfinity));
        backdrop.Arrange(new Rect(backdrop.DesiredSize));
        backdrop.UpdateLayout();

        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(backdrop.ActualWidth * 1.5), (int)Math.Ceiling(backdrop.ActualHeight * 1.5), 144, 144, PixelFormats.Pbgra32);
        bmp.Render(backdrop);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = File.Create(outputPath)) encoder.Save(fs);
        w._closing = true;
        w.Close();
    }

    private void ApplyThemeFixed(bool dark)
    {
        _p = dark ? Palette.Dark : Palette.Light;
        _panel.Background = _p.Tint;
        _panel.BorderBrush = _p.Border;
        _glass.Foreground = _p.SubText;
        _placeholder.Foreground = _p.SubText;
        _query.Foreground = _p.Text;
        _divider.Background = _p.Divider;
    }

    // ===================== Windows API =====================

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_SPACE = 0x20;

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

// ===================================================================
//  検索の中身（アプリとファイルの一覧を持っておき、入力のたびに絞り込む）
// ===================================================================

public enum SearchKind { App, Folder, File, Calc }

public sealed class SearchItem
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required SearchKind Kind { get; init; }
    public string Detail { get; init; } = "";

    /// <summary>探すとき用の、表記ゆれをそろえた名前</summary>
    public string Key { get; init; } = "";

    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? Icon()
    {
        if (Kind == SearchKind.Calc) return null;
        if (IconCache.TryGetValue(Path, out var cached)) return cached;
        ImageSource? icon = null;
        try { icon = IconHelper.GetIcon(Path, 64); } catch { }
        if (IconCache.Count > 2000) IconCache.Clear();
        IconCache[Path] = icon;
        return icon;
    }
}

public sealed class SearchResults
{
    public SearchItem? Top { get; set; }
    public List<SearchItem> Apps { get; } = new();
    public List<SearchItem> Folders { get; } = new();
    public List<SearchItem> Files { get; } = new();
    public bool IsEmpty => Top == null && Apps.Count == 0 && Folders.Count == 0 && Files.Count == 0;
}

public static class SearchIndex
{
    private const int MaxFiles = 80_000;
    private const int MaxDepth = 8;
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", "bin", "obj", "AppData", "$RECYCLE.BIN", "__pycache__", ".venv", "venv", ".vs", ".idea",
    };

    private static volatile List<SearchItem> _apps = new();
    private static volatile List<SearchItem> _files = new();
    private static DateTime _builtAt = DateTime.MinValue;
    private static int _building;

    /// <summary>一覧が古ければ、裏で作り直す（作り終わるまでは古い一覧で探す）</summary>
    public static void RefreshInBackground()
    {
        if (DateTime.Now - _builtAt < MaxAge) return;
        if (Interlocked.Exchange(ref _building, 1) == 1) return;

        // アプリ一覧は Shell の COM を使うので、STA のスレッドで作る
        var thread = new Thread(() =>
        {
            try { Build(); }
            finally { Interlocked.Exchange(ref _building, 0); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>今すぐ作る（開発用プレビュー向け）</summary>
    public static void RefreshNow() => Build();

    private static void Build()
    {
        var apps = new List<SearchItem>();
        try
        {
            foreach (var a in InstalledApps.FindAll())
                apps.Add(new SearchItem { Name = a.Name, Path = a.Path, Kind = SearchKind.App, Detail = Loc.T("アプリケーション"), Key = Normalize(a.Name) });
        }
        catch
        {
        }
        _apps = apps;
        _files = ScanFiles();
        _builtAt = DateTime.Now;
    }

    private static List<SearchItem> ScanFiles()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Path.Combine(home, "OneDrive"),
        };

        var list = new List<SearchItem>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Dir, int Depth)>();
        foreach (var r in roots)
            if (!string.IsNullOrEmpty(r) && Directory.Exists(r) && visited.Add(r)) queue.Enqueue((r, 0));

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        while (queue.Count > 0 && list.Count < MaxFiles)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    if (list.Count >= MaxFiles) break;
                    bool isDir = info is DirectoryInfo;
                    if (isDir && SkipDirs.Contains(info.Name)) continue;
                    if (!isDir && info.Name.StartsWith("~$")) continue; // Office の一時ファイル

                    list.Add(new SearchItem
                    {
                        Name = info.Name,
                        Path = info.FullName,
                        Kind = isDir ? SearchKind.Folder : SearchKind.File,
                        Detail = ShortPath(Path.GetDirectoryName(info.FullName) ?? "", home),
                        Key = Normalize(isDir ? info.Name : Path.GetFileNameWithoutExtension(info.Name)) + "\u0001" + Normalize(info.Name),
                    });

                    if (isDir && depth < MaxDepth && visited.Add(info.FullName)) queue.Enqueue((info.FullName, depth + 1));
                }
            }
            catch
            {
            }
        }
        return list;
    }

    /// <summary>入っているフォルダーを、Mac 風に「親 › フォルダー」の形で短く表す</summary>
    private static string ShortPath(string path, string home)
    {
        if (path.Equals(home, StringComparison.OrdinalIgnoreCase)) return Loc.T("ホーム");
        var parts = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => path,
            1 => parts[0],
            _ => parts[^2] + " › " + parts[^1],
        };
    }

    /// <summary>大文字・小文字、全角・半角、カタカナ・ひらがなの違いをなくす</summary>
    public static string Normalize(string s)
    {
        s = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c);
        return sb.ToString();
    }

    public static SearchResults Search(string query)
    {
        var results = new SearchResults();
        var words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return results;

        var calc = TryCalculate(query);
        var scored = new List<(SearchItem Item, int Score)>();

        foreach (var app in _apps)
        {
            int s = Score(app.Key, words);
            if (s >= 0) scored.Add((app, s + 25)); // 同じくらいならアプリを優先
        }
        var appHits = scored.OrderByDescending(x => x.Score).ThenBy(x => x.Item.Name.Length).Take(6).ToList();

        var fileHits = new List<(SearchItem Item, int Score)>();
        foreach (var file in _files)
        {
            int s = Score(file.Key, words);
            if (s >= 0) fileHits.Add((file, s));
        }
        var topFiles = fileHits.OrderByDescending(x => x.Score).ThenBy(x => x.Item.Name.Length).Take(24).ToList();

        // トップヒット: 計算の答え > いちばん合っているもの
        if (calc != null)
        {
            results.Top = calc;
        }
        else
        {
            var best = appHits.Concat(topFiles).OrderByDescending(x => x.Score).FirstOrDefault();
            results.Top = best.Item;
        }

        results.Apps.AddRange(appHits.Select(x => x.Item).Where(i => i != results.Top).Take(5));
        results.Folders.AddRange(topFiles.Where(x => x.Item.Kind == SearchKind.Folder && x.Item != results.Top).Select(x => x.Item).Take(4));
        results.Files.AddRange(topFiles.Where(x => x.Item.Kind == SearchKind.File && x.Item != results.Top).Select(x => x.Item).Take(8));
        return results;
    }

    /// <summary>すべての言葉が含まれていれば点数（先頭一致 > 単語の頭 > 途中）、含まれなければ -1</summary>
    private static int Score(string key, string[] words)
    {
        int total = 0;
        foreach (var w in words)
        {
            int idx = key.IndexOf(w, StringComparison.Ordinal);
            if (idx < 0) return -1;
            if (idx == 0) total += 100;
            else if (!char.IsLetterOrDigit(key[idx - 1])) total += 70;
            else total += 40 - Math.Min(idx, 20);
        }
        // 短い名前ほど「ぴったり」に近い
        int nameLength = key.IndexOf('\u0001') is int sep and >= 0 ? sep : key.Length;
        return total - Math.Min(nameLength, 60) / 3;
    }

    private static SearchItem? TryCalculate(string query)
    {
        string q = query.Replace('×', '*').Replace('÷', '/').Replace(" ", "");
        q = q.Normalize(NormalizationForm.FormKC);
        if (q.Length < 3 || !q.Any(c => c is '+' or '-' or '*' or '/' or '%')) return null;
        if (!q.All(c => char.IsDigit(c) || c is '+' or '-' or '*' or '/' or '%' or '.' or '(' or ')')) return null;
        try
        {
            var value = new System.Data.DataTable().Compute(q, null);
            double d = Convert.ToDouble(value);
            if (double.IsNaN(d) || double.IsInfinity(d)) return null;
            string text = Math.Round(d, 10).ToString("G15");
            return new SearchItem { Name = "= " + text, Path = "", Kind = SearchKind.Calc, Detail = Loc.T("Enter でコピー") };
        }
        catch
        {
            return null;
        }
    }
}
