using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using static Islandock.NativeMethods;

namespace Islandock;

/// <summary>
/// Islandock の設定画面（Dock の背景・デスクトップを右クリック →「Islandock の設定...」）。
/// 変更はその場で保存して、Dock・アイランド・時計にすぐ反映する。
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    private readonly DockSettings _dock = DockConfig.LoadSettings();
    private readonly IslandSettings _island = IslandConfig.Load();
    private readonly List<CheckBox> _islandChildren = new();
    private TextBlock? _transparencyValue;

    /// <summary>設定画面を開く（すでに開いていれば手前に出す）</summary>
    public static void Open()
    {
        if (_instance != null)
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            return;
        }
        _instance = new SettingsWindow();
        _instance.Show();
        _instance.Activate();
    }

    public SettingsWindow()
    {
        InitializeComponent();
        Loc.Apply(this);
        ApplyColors();
        Build();
        App.SettingsChanged += OnSettingsChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBar();
    }

    protected override void OnClosed(EventArgs e)
    {
        App.SettingsChanged -= OnSettingsChanged;
        if (_instance == this) _instance = null;
        base.OnClosed(e);
    }

    private void OnSettingsChanged()
    {
        ApplyColors();
        ApplyTitleBar();
    }

    // ===== 色（ライト / ダーク） =====

    private static SolidColorBrush B(uint argb)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        b.Freeze();
        return b;
    }

    private void ApplyColors()
    {
        bool dark = DockTheme.IsDarkMode();
        Resources["S.Bg"] = B(dark ? 0xFF1C1C1Eu : 0xFFF2F2F7u);
        Resources["S.Card"] = B(dark ? 0xFF2C2C2Eu : 0xFFFFFFFFu);
        Resources["S.Hover"] = B(dark ? 0xFF3A3A3Cu : 0xFFE9E9EEu);
        Resources["S.Fg"] = B(dark ? 0xFFF2F2F7u : 0xFF1D1D1Fu);
        Resources["S.Sub"] = B(dark ? 0xFF98989Du : 0xFF8A8A8Eu);
        Resources["S.Line"] = B(dark ? 0xFF3A3A3Cu : 0xFFE3E3E8u);
        Resources["S.SwitchOff"] = B(dark ? 0xFF48484Au : 0xFFD9D9DEu);
        Resources["S.SegBg"] = B(dark ? 0xFF1C1C1Eu : 0xFFE6E6EBu);
        Resources["S.SegSelected"] = B(dark ? 0xFF636366u : 0xFFFFFFFFu);
        Resources["S.Accent"] = B(0xFF2D7CF6);
    }

    /// <summary>タイトルバーもダーク / ライトに合わせる（Windows 10 20H1 以降）</summary>
    private void ApplyTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = DockTheme.IsDarkMode() ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    // ===== 画面を組み立てる =====

    private void Build()
    {
        Body.Children.Clear();
        _islandChildren.Clear();

        var title = Text("Islandock の設定", 22, "S.Fg");
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(4, 0, 0, 14);
        Body.Children.Add(title);

        // --- 一般 ---
        Section("一般");
        Card(
            Row("外観", "Dock・メニュー・時計ウィジェットの色", ThemePicker()),
            Row("言語", "変えると Islandock が再起動します", LanguagePicker()),
            Toggle("PC の起動時に Islandock を開く", "Windows にサインインしたときに自動で起動します",
                StartupRegistration.IsEnabled, StartupRegistration.SetEnabled),
            Toggle("デスクトップの右クリックに Islandock のメニューを出す",
                "デスクトップの何もないところを右クリックすると、この設定画面を開けます",
                _dock.DesktopMenu, on => { _dock.DesktopMenu = on; SaveDock(); return true; }),
            Toggle("検索バー", Loc.F("{0} で、アプリとファイルを探せる検索バーを出します", SearchWindow.HotkeyText),
                _dock.QuickSearch, on => { _dock.QuickSearch = on; SaveDock(); return true; }),
            Toggle("検索バーを常にデスクトップに表示", "キーを押さなくても、画面の中央（ウィンドウの後ろ）に置いておきます",
                _dock.SearchPinned, on => { _dock.SearchPinned = on; SaveDock(); return true; }),
            Toggle("アップデートを自動で確認", Loc.F("起動時に新しいバージョンがないか確かめます（いまはバージョン {0}）", Updater.CurrentVersion),
                _dock.AutoUpdateCheck, on => { _dock.AutoUpdateCheck = on; SaveDock(); return true; }));

        // --- Dock ---
        Section("Dock");
        Card(
            TransparencyRow(),
            Toggle("拡大", "マウスを近づけたアイコンを大きくします",
                _dock.Magnification, on => { _dock.Magnification = on; SaveDock(); return true; }),
            Toggle("Dock を自動的に表示 / 非表示", "画面の下端にマウスを当てたときだけ出します",
                _dock.AutoHide, on => { _dock.AutoHide = on; SaveDock(); return true; }),
            Toggle("Windows のタスクバーを隠す", "Dock だけにします。Islandock を終了すると元に戻ります",
                _dock.HideTaskbar, on => { _dock.HideTaskbar = on; SaveDock(); return true; }),
            Toggle("開く / しまうときのアニメーション（ジニー）", null,
                _dock.OpenAnimation, on => { _dock.OpenAnimation = on; SaveDock(); return true; }),
            Toggle("アイコンをタイルに揃える", null,
                _dock.IconTiles, on => { _dock.IconTiles = on; SaveDock(); return true; }),
            Toggle("デスクトップのアプリを自動で追加", null,
                _dock.AutoAddDesktopApps, on => { _dock.AutoAddDesktopApps = on; SaveDock(); return true; }),
            Toggle("ごみ箱を表示", null,
                _dock.ShowTrash, on => { _dock.ShowTrash = on; SaveDock(); return true; }),
            Toggle("時計ウィジェットを表示", "デスクトップにアナログ時計を置きます（右クリックでスタイルを変更）",
                ClockWidget.IsShown, on =>
                {
                    if (on) ClockWidget.ShowWidget();
                    else ClockWidget.HideWidget();
                    return true;
                }));

        // --- アイランド ---
        Section("アイランド");
        var master = Toggle("アイランドを使う", "画面上部の真ん中に出るカプセル型のバー",
            _island.Enabled, on => { _island.Enabled = on; SaveIsland(); UpdateIslandChildren(); return true; });
        Card(master,
            IslandChild("再生中", "再生中の曲の表示と操作",
                _island.ShowMedia, on => _island.ShowMedia = on),
            IslandChild("タイマー / ストップウォッチ", null,
                _island.ShowTimer, on => _island.ShowTimer = on),
            IslandChild("カレンダー", null,
                _island.ShowCalendar, on => _island.ShowCalendar = on),
            IslandChild("ファイル共有", "近距離共有で送る・スマホから受け取る",
                _island.ShowFileShare, on => _island.ShowFileShare = on),
            IslandChild("お知らせ", "音量・充電・出力先の切り替えなど",
                _island.ShowAlerts, on => _island.ShowAlerts = on),
            IslandChild("マイク / カメラのランプ", "使っている間、アイランドの右に点を表示します",
                _island.ShowPrivacy, on => _island.ShowPrivacy = on));
        UpdateIslandChildren();

        // --- 下のボタン ---
        var footer = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        footer.Children.Add(FooterButton("設定フォルダーを開く", () =>
            Process.Start(new ProcessStartInfo(System.IO.Path.GetDirectoryName(DockConfig.ConfigPath)!) { UseShellExecute = true })));
        footer.Children.Add(FooterButton("アップデートを確認", () => _ = Updater.CheckAsync(manual: true)));
        footer.Children.Add(FooterButton("Islandock を終了", () => Application.Current.Shutdown()));
        Body.Children.Add(footer);
    }

    private static void SaveDock()
    {
        DockConfig.SaveSettings(DockConfig.LoadSettings());
        App.NotifySettingsChanged();
    }

    private static void SaveIsland()
    {
        IslandConfig.Save(IslandConfig.Load());
        App.NotifySettingsChanged();
    }

    private void UpdateIslandChildren()
    {
        foreach (var c in _islandChildren) c.IsEnabled = _island.Enabled;
    }

    // ===== 部品 =====

    /// <summary>文字の部品。渡された日本語はここで今の言語に訳す（表にない文字はそのまま）</summary>
    private static TextBlock Text(string text, double size, string brushKey)
    {
        var t = new TextBlock { Text = Loc.T(text), FontSize = size, TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        return t;
    }

    private void Section(string name)
    {
        var t = Text(name, 12, "S.Sub");
        t.FontWeight = FontWeights.SemiBold;
        t.Margin = new Thickness(12, Body.Children.Count > 1 ? 18 : 0, 0, 6);
        Body.Children.Add(t);
    }

    /// <summary>角の丸い白い板に、行を区切り線で並べる</summary>
    private void Card(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                var line = new Border { Height = 1, Margin = new Thickness(14, 0, 0, 0) };
                line.SetResourceReference(Border.BackgroundProperty, "S.Line");
                stack.Children.Add(line);
            }
            stack.Children.Add(rows[i]);
        }
        var card = new Border { CornerRadius = new CornerRadius(10), Child = stack, BorderThickness = new Thickness(0.5) };
        card.SetResourceReference(Border.BackgroundProperty, "S.Card");
        card.SetResourceReference(Border.BorderBrushProperty, "S.Line");
        Body.Children.Add(card);
    }

    /// <summary>左に名前と説明、右に操作部品を置いた1行</summary>
    private static Grid Row(string title, string? sub, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(14, 9, 14, 9), MinHeight = 26 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        texts.Children.Add(Text(title, 13, "S.Fg"));
        if (sub != null)
        {
            var s = Text(sub, 11.5, "S.Sub");
            s.Margin = new Thickness(0, 1, 0, 0);
            texts.Children.Add(s);
        }
        grid.Children.Add(texts);

        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>スイッチの行。onChange が false を返したら元に戻す</summary>
    private static Grid Toggle(string title, string? sub, bool value, Func<bool, bool> onChange) =>
        Toggle(title, sub, value, onChange, out _);

    private static Grid Toggle(string title, string? sub, bool value, Func<bool, bool> onChange, out CheckBox box)
    {
        var sw = new CheckBox { IsChecked = value };
        sw.SetResourceReference(StyleProperty, "Switch");
        bool reverting = false;
        RoutedEventHandler handler = (_, _) =>
        {
            if (reverting) return;
            if (!onChange(sw.IsChecked == true))
            {
                reverting = true;
                sw.IsChecked = !sw.IsChecked;
                reverting = false;
            }
        };
        sw.Checked += handler;
        sw.Unchecked += handler;
        box = sw;

        var row = Row(title, sub, sw);
        // 行のどこを押しても切り替えられるように
        row.Background = Brushes.Transparent;
        row.Cursor = System.Windows.Input.Cursors.Hand;
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && IsInside(d, sw)) return;
            if (sw.IsEnabled) sw.IsChecked = !sw.IsChecked;
        };
        return row;
    }

    private static bool IsInside(DependencyObject d, DependencyObject parent)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x))
            if (x == parent) return true;
        return false;
    }

    /// <summary>アイランドの各機能のスイッチ（少し字下げして、「アイランドを使う」がオフなら押せなくする）</summary>
    private Grid IslandChild(string title, string? sub, bool value, Action<bool> set)
    {
        var row = Toggle(title, sub, value, on => { set(on); SaveIsland(); return true; }, out var box);
        row.Margin = new Thickness(30, row.Margin.Top, row.Margin.Right, row.Margin.Bottom);
        _islandChildren.Add(box);
        box.IsEnabledChanged += (_, _) => row.Opacity = box.IsEnabled ? 1 : 0.45;
        return row;
    }

    private FrameworkElement ThemePicker()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, value) in new[] { ("自動", "System"), ("ライト", "Light"), ("ダーク", "Dark") })
        {
            var rb = new RadioButton
            {
                Content = Loc.T(label),
                GroupName = "Theme",
                IsChecked = _dock.Theme == value || (value == "System" && _dock.Theme is not ("Light" or "Dark")),
            };
            rb.SetResourceReference(StyleProperty, "Segment");
            rb.Checked += (_, _) =>
            {
                _dock.Theme = value;
                SaveDock();
            };
            panel.Children.Add(rb);
        }
        var bg = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = panel };
        bg.SetResourceReference(Border.BackgroundProperty, "S.SegBg");
        return bg;
    }

    /// <summary>表示する言語（それぞれの言語の名前で並べる。選ぶと保存して再起動する）</summary>
    private FrameworkElement LanguagePicker()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, code, lang) in new[]
                 {
                     ("日本語", "ja", UiLanguage.Japanese),
                     ("English", "en", UiLanguage.English),
                     ("हिन्दी", "hi", UiLanguage.Hindi),
                 })
        {
            var rb = new RadioButton { Content = label, GroupName = "Language", IsChecked = Loc.Current == lang };
            rb.SetResourceReference(StyleProperty, "Segment");
            rb.Checked += (_, _) =>
            {
                if (Loc.Current != lang) Dispatcher.BeginInvoke(() => Loc.ChangeAndRestart(code));
            };
            panel.Children.Add(rb);
        }
        var bg = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(2), Child = panel };
        bg.SetResourceReference(Border.BackgroundProperty, "S.SegBg");
        return bg;
    }

    /// <summary>Dock の背景の透明度（スライダー）</summary>
    private FrameworkElement TransparencyRow()
    {
        _transparencyValue = Text("", 13, "S.Sub");
        var head = Row("背景の透明度", "大きくするほど Dock の背景が透けます（100% で背景なし）", _transparencyValue);
        head.Margin = new Thickness(14, 9, 14, 2);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(_dock.DockTransparency, 0, 100),
            SmallChange = 1,
            LargeChange = 10,
            Margin = new Thickness(14, 0, 14, 10),
        };
        slider.SetResourceReference(StyleProperty, "ThinSlider");
        _transparencyValue.Text = $"{Math.Round(slider.Value)}%";

        // 動かしている間はすぐ反映し、保存は離したときだけ
        slider.ValueChanged += (_, _) =>
        {
            _dock.DockTransparency = Math.Round(slider.Value);
            _transparencyValue.Text = $"{_dock.DockTransparency}%";
            App.NotifySettingsChanged();
        };
        slider.PreviewMouseLeftButtonUp += (_, _) => DockConfig.SaveSettings(_dock);
        slider.LostMouseCapture += (_, _) => DockConfig.SaveSettings(_dock);

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(slider);
        return stack;
    }

    private static Button FooterButton(string text, Action onClick)
    {
        var b = new Button { Content = Loc.T(text), Margin = new Thickness(0, 0, 8, 8) };
        b.SetResourceReference(StyleProperty, "FooterButton");
        b.Click += (_, _) => onClick();
        return b;
    }

    // ===== 開発用 =====

    /// <summary>設定画面を PNG に書き出す（README・確認用）</summary>
    internal static void RenderPreview(string path, bool dark)
    {
        var settings = DockConfig.LoadSettings();
        string saved = settings.Theme;
        settings.Theme = dark ? "Dark" : "Light";
        var w = new SettingsWindow { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, Height = 1200 };
        w.Show();
        w.UpdateLayout();

        var root = (FrameworkElement)w.Content;
        double width = root.ActualWidth, height = w.Body.ActualHeight + 40;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(width * 1.5), (int)(height * 1.5), 144, 144, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)w.Resources["S.Bg"], null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(w.Body) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(w.Body.Margin.Left, w.Body.Margin.Top, w.Body.ActualWidth, w.Body.ActualHeight));
        }
        bmp.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = System.IO.File.Create(path)) encoder.Save(fs);

        w.Close();
        settings.Theme = saved;
    }
}
