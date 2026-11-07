using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using static Islandock.NativeMethods;

namespace Islandock;

/// <summary>
/// デスクトップの「何もないところ」を右クリックしたときに、Islandock のメニューを出す。
///
/// しくみ: マウスのフック（WH_MOUSE_LL）でデスクトップ上の右クリックをいったん止め、
/// アイコンの上だったら同じ右クリックを送り直して Windows のメニューを出す（送り直した分は止めない）。
/// 何もないところなら Islandock のメニューを出し、そこから Windows のメニューも開ける。
/// </summary>
internal static class DesktopRightClick
{
    private static IntPtr _hook;
    private static HookProc? _proc; // GC に回収されないように持っておく
    private static bool _swallowing;

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = OnMouse;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) App.WriteCrashLog("デスクトップの右クリック", new System.ComponentModel.Win32Exception());
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            bool injected = (info.flags & LLMHF_INJECTED) != 0;
            int msg = (int)wParam;
            if (!injected)
            {
                if (msg == WM_RBUTTONDOWN && DockConfig.LoadSettings().DesktopMenu && IsDesktop(info.pt))
                {
                    _swallowing = true;
                    return (IntPtr)1;
                }
                if (msg == WM_RBUTTONUP && _swallowing)
                {
                    _swallowing = false;
                    var pt = info.pt;
                    Application.Current.Dispatcher.BeginInvoke(() => Decide(pt));
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>その位置がデスクトップ（アイコンが並んでいる面）か</summary>
    private static bool IsDesktop(POINT pt)
    {
        var hwnd = WindowFromPoint(pt);
        if (ClassOf(hwnd) != "SysListView32") return false;
        var view = GetParent(hwnd);
        if (ClassOf(view) != "SHELLDLL_DefView") return false;
        return ClassOf(GetParent(view)) is "Progman" or "WorkerW";
    }

    private static string ClassOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(64);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>アイコンの上なら Windows に任せ、何もないところなら Islandock のメニューを出す</summary>
    private static async void Decide(POINT pt)
    {
        bool onIcon = await Task.Run(() => IsOnIcon(pt));
        if (onIcon) ReplayRightClick(pt);
        else ShowMenu(pt);
    }

    private static bool IsOnIcon(POINT pt)
    {
        try
        {
            var el = AutomationElement.FromPoint(new Point(pt.X, pt.Y));
            for (int i = 0; el != null && i < 3; i++)
            {
                var type = el.Current.ControlType;
                if (type == ControlType.ListItem) return true;
                if (type == ControlType.List) return false;
                el = TreeWalker.ControlViewWalker.GetParent(el);
            }
            return false;
        }
        catch
        {
            return true; // 分からないときは Windows のメニューを出す（いつもどおりの動き）
        }
    }

    /// <summary>止めた右クリックを、その位置でもう一度送る（フックは送り直した分を素通しする）</summary>
    private static void ReplayRightClick(POINT pt)
    {
        SetCursorPos(pt.X, pt.Y);
        var inputs = new[]
        {
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTDOWN } },
            new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTUP } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void ShowMenu(POINT pt)
    {
        DockTheme.Current().ApplyMenuResources();

        // メニューの持ち主になる見えない小さなウィンドウ（手前に出さないと、外をクリックしても閉じない）
        var host = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Width = 1,
            Height = 1,
            Left = -100,
            Top = -100,
        };
        host.Show();
        BringToFront(new System.Windows.Interop.WindowInteropHelper(host).Handle);

        bool replay = false;
        var menu = new ContextMenu { PlacementTarget = host };
        menu.Items.Add(Item("Islandock の設定...", SettingsWindow.Open));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("ディスプレイの設定", () => OpenUri("ms-settings:display")));
        menu.Items.Add(Item("個人用設定", () => OpenUri("ms-settings:personalization")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Windows のメニューを表示", () => replay = true));
        menu.Closed += (_, _) =>
        {
            host.Close();
            if (replay)
                Application.Current.Dispatcher.BeginInvoke(() => ReplayRightClick(pt),
                    System.Windows.Threading.DispatcherPriority.Background);
        };
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>ほかのアプリが手前にあっても、自分のウィンドウを手前に出す</summary>
    private static void BringToFront(IntPtr hwnd)
    {
        var fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        SetForegroundWindow(hwnd);
        if (attached) AttachThreadInput(me, fgThread, false);
    }

    // ===== Win32 =====

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    private const int WH_MOUSE_LL = 14;
    private const int WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    private const uint LLMHF_INJECTED = 0x01;
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    // INPUT は union を含むので、いちばん大きい KEYBDINPUT / HARDWAREINPUT より MOUSEINPUT が大きいことを利用
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
