using System.Windows;

namespace Islandock;

/// <summary>
/// アプリの入口。二重起動を防いでから Dock を表示する。
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private static IslandWindow? _island;

    /// <summary>設定画面で設定が変わったときに呼ばれる（Dock・アイランドが見た目を更新する）</summary>
    public static event Action? SettingsChanged;

    /// <summary>設定を変えたら呼ぶ。アイランドの表示 / 非表示・色などをまとめて反映する</summary>
    public static void NotifySettingsChanged()
    {
        DockTheme.Current().ApplyMenuResources();
        ApplyIslandEnabled();
        ClockWidget.RefreshTheme();
        SettingsChanged?.Invoke();
    }

    /// <summary>アイランドを使う設定ならアイランドを出し、使わないなら隠す</summary>
    private static void ApplyIslandEnabled()
    {
        if (IslandConfig.Load().Enabled)
        {
            _island ??= new IslandWindow();
            if (!_island.IsVisible) _island.Show();
        }
        else
        {
            _island?.Hide();
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MigrateOldSettings(); // 設定フォルダーが作られる前に、いちばん最初に行う
        HookCrashLog();

        // 開発用: Islandock.exe --preview 出力.png で、Dock のアイコンを画像に書き出して終了する
        if (e.Args.Length == 2 && e.Args[0] == "--preview")
        {
            Preview.Render(e.Args[1]);
            Shutdown();
            return;
        }

        // 開発用: Islandock.exe --preview-demo 出力.png で、Windows 標準アプリだけの Dock を書き出す（README 用）
        if (e.Args.Length == 2 && e.Args[0] == "--preview-demo")
        {
            Preview.RenderDemo(e.Args[1]);
            Shutdown();
            return;
        }

        // 開発用: Islandock.exe --export-icons 出力フォルダー [dark] で、紹介動画用の大きなアイコンを書き出す
        if (e.Args.Length is 2 or 3 && e.Args[0] == "--export-icons")
        {
            Preview.ExportDemoIcons(e.Args[1], dark: e.Args.Length == 3 && e.Args[2] == "dark");
            Shutdown();
            return;
        }

        // 開発用: Islandock.exe --export-island-demo 出力フォルダー で、アイランドのいろいろな状態を架空のデータで書き出す
        if (e.Args.Length == 2 && e.Args[0] == "--export-island-demo")
        {
            var island = new IslandWindow { Left = -10000, Top = -10000, ShowActivated = false };
            island.Show();
            island.ExportDemoStates(e.Args[1]);
            island.Close();
            Shutdown();
            return;
        }

        // 開発用: Islandock.exe --find-windows 出力.txt で、Dock の各アプリのウィンドウとして見つかるものを書き出す
        if (e.Args.Length == 2 && e.Args[0] == "--find-windows")
        {
            System.IO.File.WriteAllLines(e.Args[1], DockWindow.DescribeAppWindows());
            Shutdown();
            return;
        }

        // 開発用: Islandock.exe --genie-roundtrip ウィンドウ番号(16進) 出力.txt で、ジニーでしまって戻すのを試す
        if (e.Args.Length == 3 && e.Args[0] == "--genie-roundtrip")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            GenieRoundTrip(new IntPtr(Convert.ToInt64(e.Args[1], 16)), e.Args[2]);
            return;
        }

        // 開発用: Islandock.exe --launch-test exe 出力.txt / --close-test ウィンドウ番号(16進)
        //   起動・終了のアニメーションを、Dock を使わずに試す（アイコンの位置は画面下の中央とみなす）
        if (e.Args.Length == 3 && e.Args[0] == "--launch-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var (icon, dpi) = TestIcon();
            string exe = e.Args[1], output = e.Args[2];
            string name = System.IO.Path.GetFileNameWithoutExtension(exe);
            string? host = AppWindows.IsConsoleApp(exe) ? "WindowsTerminal" : null;
            Genie.WatchForNewWindow(name, NativeMethods.GetVisibleTopWindows(), h =>
            {
                System.IO.File.WriteAllText(output, h.ToInt64().ToString("X"));
                Genie.Open(h, icon, dpi);
            }, TimeSpan.FromSeconds(10), host);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
            QuitAfter(5);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--close-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Genie.AnimationsEnabled = () => true;
            var trace = new List<string>();
            Genie.CloseTrace = m => trace.Add($"{DateTime.Now:HH:mm:ss.fff} {m}");
            Genie.CloseWithAnimation(new IntPtr(Convert.ToInt64(e.Args[1], 16)));
            Exit += (_, _) => System.IO.File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "islandock_close_trace.txt"), trace);
            QuitAfter(3);
            return;
        }

        // 開発用: Islandock.exe --list-apps 出力.txt で、アプリ一覧を書き出す
        if (e.Args.Length == 2 && e.Args[0] == "--list-apps")
        {
            System.IO.File.WriteAllLines(e.Args[1], InstalledApps.FindAll().Select(a => $"{a.Source} | {a.Name} | {a.Path}"));
            Shutdown();
            return;
        }

        // 開発用: Islandock.exe --preview-island (compact|calendar|media|timer|airdrop|phone) 出力.png
        if (e.Args.Length == 3 && e.Args[0] == "--preview-island")
        {
            var island = new IslandWindow { Left = -10000, Top = -10000, ShowActivated = false };
            island.Show();
            island.RenderPreview(e.Args[2], e.Args[1]);
            island.Close();
            Shutdown();
            return;
        }

        // 開発用: Islandock.exe --genie-test notepad で、ジニーエフェクトを試す
        if (e.Args.Length == 2 && e.Args[0] == "--genie-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var area = SystemParameters.WorkArea;
            double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(new System.Windows.Controls.Border()).DpiScaleX;
            var icon = new Rect((area.Left + area.Width / 2 - 24) * dpi, (area.Bottom - 60) * dpi, 48 * dpi, 48 * dpi);
            Genie.WatchForNewWindow(e.Args[1], new HashSet<IntPtr>(), hwnd => Genie.Open(hwnd, icon, dpi), TimeSpan.FromSeconds(10));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Args[1]) { UseShellExecute = true });
            var quit = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            quit.Tick += (_, _) => Shutdown();
            quit.Start();
            return;
        }

        // 開発用: Islandock.exe --phone-test 保存先フォルダー で、スマホ受信サーバーを 60 秒だけ動かす（URL は url.txt に）
        if (e.Args.Length == 2 && e.Args[0] == "--phone-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var rx = new PhoneReceiver { SaveFolder = e.Args[1] };
            rx.Start();
            System.IO.File.WriteAllText(System.IO.Path.Combine(e.Args[1], "url.txt"), rx.Url);
            var quit = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            quit.Tick += (_, _) => { rx.Stop(); Shutdown(); };
            quit.Start();
            return;
        }

        // 開発用: Islandock.exe --preview-settings (light|dark) 出力.png
        if (e.Args.Length == 3 && e.Args[0] == "--preview-settings")
        {
            SettingsWindow.RenderPreview(e.Args[2], e.Args[1] == "dark");
            Shutdown();
            return;
        }

        // 開発用: Islandock.exe --preview-clock 出力.png
        if (e.Args.Length == 2 && e.Args[0] == "--preview-clock")
        {
            ClockWidget.RenderPreview(e.Args[1]);
            Shutdown();
            return;
        }

        // すでに Islandock が動いていたら、2つ目は起動しない
        _mutex = new Mutex(true, "Islandock_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        new DockWindow().Show();
        ApplyIslandEnabled();
        ClockWidget.ShowIfEnabled();
        DesktopRightClick.Start();
        StartupRegistration.RefreshPath();
    }

    private async void GenieRoundTrip(IntPtr hwnd, string output)
    {
        var log = new List<string>();
        string State() => $"iconic={NativeMethods.IsIconic(hwnd)} visible={NativeMethods.IsWindowVisible(hwnd)} " +
                          $"layered={(NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE) & 0x80000) != 0}";
        var area = SystemParameters.WorkArea;
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(new System.Windows.Controls.Border()).DpiScaleX;
        var icon = new Rect((area.Left + area.Width / 2 - 24) * dpi, (area.Bottom - 60) * dpi, 48 * dpi, 48 * dpi);
        log.Add("before:   " + State());
        Genie.Minimize(hwnd, icon, dpi);
        await Task.Delay(1500);
        log.Add("minimized:" + State());
        Genie.Open(hwnd, icon, dpi);
        await Task.Delay(1500);
        log.Add("restored: " + State());
        System.IO.File.WriteAllLines(output, log);
        Shutdown();
    }

    private static (Rect icon, double dpi) TestIcon()
    {
        var area = SystemParameters.WorkArea;
        double dpi = System.Windows.Media.VisualTreeHelper.GetDpi(new System.Windows.Controls.Border()).DpiScaleX;
        return (new Rect((area.Left + area.Width / 2 - 24) * dpi, (area.Bottom - 60) * dpi, 48 * dpi, 48 * dpi), dpi);
    }

    private void QuitAfter(double seconds)
    {
        var quit = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        quit.Tick += (_, _) => Shutdown();
        quit.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DesktopRightClick.Stop();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 予期しないエラーを %APPDATA%\Islandock\crash.log に書き残す。
    /// 画面の処理中に起きたエラーは、アプリを落とさずに続ける。
    /// </summary>
    private void HookCrashLog()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            WriteCrashLog("UI", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("AppDomain (終了します)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("Task", e.Exception);
            e.SetObserved();
        };
    }

    public static void WriteCrashLog(string where, Exception? ex)
    {
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Islandock");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "crash.log"),
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{where}]\n{ex}\n\n");
        }
        catch
        {
        }
    }

    /// <summary>
    /// 旧名（MacDesk）のころの設定フォルダーがあれば、新しい名前のフォルダーにコピーして引き継ぐ。
    /// 古いフォルダーは消さずに残す。
    /// </summary>
    private static void MigrateOldSettings()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string oldDir = System.IO.Path.Combine(appData, "MacDesk");
            string newDir = System.IO.Path.Combine(appData, "Islandock");
            if (System.IO.Directory.Exists(newDir) || !System.IO.Directory.Exists(oldDir)) return;
            System.IO.Directory.CreateDirectory(newDir);
            foreach (var file in System.IO.Directory.GetFiles(oldDir))
                System.IO.File.Copy(file, System.IO.Path.Combine(newDir, System.IO.Path.GetFileName(file)));
        }
        catch
        {
        }
    }
}
