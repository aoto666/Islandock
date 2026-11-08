using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace Islandock;

/// <summary>
/// Windows のタスクバーを隠して、Dock だけにする。
///
/// 仕組み:
///  1. Windows の「タスクバーを自動的に隠す」をオンにする（画面の下端まで作業領域が広がり、Dock がいちばん下に来る）
///  2. タスクバーのウィンドウ自体を非表示にする（自動で隠すだけだと、マウスを下端に当てたときに出てきてしまう）
///  3. Islandock を終了したら、元の状態に戻す
///
/// 元の状態はファイルに書いておくので、Islandock が異常終了しても次に起動したときに戻せる。
/// </summary>
public static class TaskbarHider
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Islandock", "taskbar-state.txt");

    private static DispatcherTimer? _keepHidden;

    public static bool IsHiding => _keepHidden != null;

    /// <summary>設定に合わせて、隠す / 元に戻す</summary>
    public static void Apply(bool hide)
    {
        if (hide) Hide();
        else Restore();
    }

    private static void Hide()
    {
        // 最初に隠すときだけ、元の「自動的に隠す」の設定を覚えておく
        if (!File.Exists(StatePath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                File.WriteAllText(StatePath, GetAppBarState().ToString());
            }
            catch
            {
                return; // 元に戻せなくなるので、覚えておけないときは隠さない
            }
        }

        SetAppBarState(GetAppBarState() | ABS_AUTOHIDE);
        HideTrayWindows();

        if (_keepHidden == null)
        {
            // スタートメニューを開いたときや、エクスプローラーが再起動したときに、タスクバーがまた出てくるので隠し直す
            _keepHidden = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _keepHidden.Tick += (_, _) => HideTrayWindows();
            _keepHidden.Start();
        }
    }

    /// <summary>タスクバーを元に戻す（隠していなければ何もしない）</summary>
    public static void Restore()
    {
        _keepHidden?.Stop();
        _keepHidden = null;

        if (!File.Exists(StatePath)) return;

        int original = ABS_ALWAYSONTOP;
        try
        {
            if (int.TryParse(File.ReadAllText(StatePath).Trim(), out int saved)) original = saved;
        }
        catch
        {
        }

        SetAppBarState(original);
        foreach (var tray in FindTrayWindows()) ShowWindow(tray, SW_SHOWNA);

        try { File.Delete(StatePath); } catch { }
    }

    private static void HideTrayWindows()
    {
        foreach (var tray in FindTrayWindows())
            if (IsWindowVisible(tray)) ShowWindow(tray, SW_HIDE);
    }

    /// <summary>メインのタスクバーと、ほかのモニターのタスクバー</summary>
    private static List<IntPtr> FindTrayWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            var cls = new StringBuilder(64);
            GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static int GetAppBarState()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return (int)SHAppBarMessage(ABM_GETSTATE, ref data);
    }

    private static void SetAppBarState(int state)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), lParam = (IntPtr)state };
        SHAppBarMessage(ABM_SETSTATE, ref data);
    }

    // ===================== Windows API =====================

    private const uint ABM_GETSTATE = 4;
    private const uint ABM_SETSTATE = 10;
    private const int ABS_AUTOHIDE = 1;
    private const int ABS_ALWAYSONTOP = 2;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNA = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("shell32.dll")] private static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
}
