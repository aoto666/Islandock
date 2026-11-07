using System.Windows;

namespace Islandock;

/// <summary>
/// アプリの入口。二重起動を防いでから Dock を表示する。
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MigrateOldSettings(); // 設定フォルダーが作られる前に、いちばん最初に行う

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
        new IslandWindow().Show();
        ClockWidget.ShowIfEnabled();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
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
