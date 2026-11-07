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
            Genie.Trace = m => trace.Add($"{DateTime.Now:HH:mm:ss.fff} {m}");
            Genie.CloseWithAnimation(new IntPtr(Convert.ToInt64(e.Args[1], 16)));
            Exit += (_, _) => System.IO.File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "islandock_close_trace.txt"), trace);
            QuitAfter(3);
            return;
        }

        // 開発用: Islandock.exe --anim-test 出力.txt "exe|引数" ... で、いろいろなアプリで格納・終了のアニメーションを確かめる
        if (e.Args.Length >= 3 && e.Args[0] == "--anim-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            AnimTest(e.Args[1], e.Args.Skip(2).ToArray());
            return;
        }

        // 開発用: Islandock.exe --preview-test 出力.txt Dockの名前 で、ウィンドウ一覧の並び方を確かめる
        if (e.Args.Length == 3 && e.Args[0] == "--preview-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.InvokeAsync(async () =>
            {
                var lines = await DockWindow.PreviewTest(e.Args[2]);
                System.IO.File.WriteAllLines(e.Args[1], lines);
                Shutdown();
            });
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

    private async void AnimTest(string output, string[] targets)
    {
        var log = new List<string>();
        var (icon, dpi) = TestIcon();
        Genie.AnimationsEnabled = () => true;
        Genie.WatchTitleBarMinimize(_ => true, _ => icon, () => dpi);
        var trace = new List<string>();
        Genie.Trace = m => trace.Add(m);
        uint own = (uint)Environment.ProcessId;

        foreach (var spec in targets)
        {
            var parts = spec.Split('|');
            log.Add($"===== {parts[0]} {(parts.Length > 1 ? parts[1] : "")}");
            try
            {
                var before = NativeMethods.GetVisibleTopWindows();
                IntPtr given = parts[0].StartsWith("hwnd:") ? new IntPtr(Convert.ToInt64(parts[0][5..], 16)) : IntPtr.Zero;
                if (given == IntPtr.Zero) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(parts[0], parts.Length > 1 ? parts[1] : "") { UseShellExecute = true });
                IntPtr hwnd = given;
                for (int i = 0; i < 60 && hwnd == IntPtr.Zero; i++)
                {
                    await Task.Delay(200);
                    hwnd = NativeMethods.GetVisibleTopWindows().FirstOrDefault(h =>
                    {
                        if (before.Contains(h) || !NativeMethods.IsLikelyAppWindow(h)) return false;
                        NativeMethods.GetWindowThreadProcessId(h, out uint pid);
                        return pid != own;
                    });
                }
                if (hwnd == IntPtr.Zero) { log.Add("  ウィンドウが見つからない"); continue; }
                await Task.Delay(1500); // 中身が描かれるまで待つ
                var cls = new System.Text.StringBuilder(128);
                NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
                log.Add($"  window {hwnd.ToInt64():X} class={cls}");

                // --- 格納（最小化） ---
                BringToFront(hwnd);
                await Task.Delay(400);
                var (mx, my) = Genie.DevButtonPoint(hwnd, 2);
                int hit = Genie.DevDetect(hwnd, mx, my, out IntPtr top);
                log.Add($"  最小化ボタン ({mx},{my}): 判定={(hit == Genie.DevHTMINBUTTON ? "最小化ボタン" : hit.ToString())} 手前のウィンドウ一致={top == hwnd} {Describe(top)}");
                trace.Clear();
                bool prepared = Genie.DevPrepareMinimize(hwnd);
                NativeMethods.SendMessage(hwnd, 0x0112, (IntPtr)0xF020, IntPtr.Zero); // WM_SYSCOMMAND SC_MINIMIZE（ボタンを押したのと同じ）
                await Task.Delay(1200);
                log.Add($"  格納: 準備={prepared} 最小化された={NativeMethods.IsIconic(hwnd)} 記録=[{string.Join(", ", trace)}]");

                // --- 終了 ---
                NativeMethods.ShowWindow(hwnd, 9); // SW_RESTORE
                await Task.Delay(700);
                BringToFront(hwnd);
                await Task.Delay(400);
                var (cx, cy) = Genie.DevButtonPoint(hwnd, 0);
                int chit = Genie.DevDetect(hwnd, cx, cy, out IntPtr ctop);
                log.Add($"  閉じるボタン ({cx},{cy}): 判定={(chit == Genie.DevHTCLOSE ? "閉じるボタン" : chit.ToString())} 手前のウィンドウ一致={ctop == hwnd}");
                trace.Clear();
                Genie.CloseWithAnimation(hwnd);
                await Task.Delay(1800);
                log.Add($"  終了: 閉じた={!NativeMethods.IsWindow(hwnd)} 記録=[{string.Join(", ", trace)}]");
            }
            catch (Exception ex)
            {
                log.Add("  エラー: " + ex.Message);
            }
        }
        System.IO.File.WriteAllLines(output, log);
        Shutdown();
    }

    private static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(なし)";
        var c = new System.Text.StringBuilder(128);
        NativeMethods.GetClassName(h, c, c.Capacity);
        NativeMethods.GetWindowThreadProcessId(h, out uint pid);
        string name = "";
        try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); name = p.ProcessName; } catch { }
        NativeMethods.GetWindowRect(h, out var r);
        return $"[手前={h.ToInt64():X} {c} {name} ex={NativeMethods.GetWindowLong(h, NativeMethods.GWL_EXSTYLE):X} rect={r.Left},{r.Top},{r.Right},{r.Bottom}]";
    }

    /// <summary>ほかのアプリが手前にあっても、そのウィンドウを手前に出す（開発用）</summary>
    private static void BringToFront(IntPtr hwnd)
    {
        var fg = NativeMethods.GetForegroundWindow();
        uint fgThread = NativeMethods.GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
        NativeMethods.SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

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
