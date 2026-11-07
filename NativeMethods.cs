using System.Runtime.InteropServices;

namespace Islandock;

/// <summary>
/// Windows の機能（Win32 API）を C# から呼び出すための宣言をまとめたもの。
/// </summary>
internal static class NativeMethods
{
    // ---- ウィンドウ操作 ----
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020; // マウスを素通りさせる
    public const int WS_EX_TOOLWINDOW = 0x00000080;  // Alt+Tab に表示しない
    public const int WS_EX_NOACTIVATE = 0x08000000;  // クリックしてもアクティブにならない
    public const int SW_RESTORE = 9;
    public const int SW_MINIMIZE = 6;

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static readonly IntPtr HWND_BOTTOM = new(1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOOWNERZORDER = 0x0200;

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    private static IntPtr _lastAppWindow;

    /// <summary>
    /// 手前にあるアプリのウィンドウが「最大化」または「全画面」か。
    /// Dock / アイランドを触っている間は、その直前に使っていたアプリのウィンドウで判断する。
    /// </summary>
    public static bool IsFrontAppMaximized()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid != (uint)Environment.ProcessId) _lastAppWindow = fg;
        }
        IntPtr h = _lastAppWindow;
        if (h == IntPtr.Zero || !IsWindow(h) || !IsWindowVisible(h) || IsIconic(h)) return false;

        var cls = new System.Text.StringBuilder(64);
        GetClassName(h, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false; // デスクトップ・タスクバー

        if (IsZoomed(h)) return true;

        // 全画面（F11 のブラウザ・ゲーム・動画など）は最大化扱いにならないので、画面全体を覆っているかで判断
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetWindowRect(h, out var r) || !GetMonitorInfo(MonitorFromWindow(h, 2 /* NEAREST */), ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public const uint GW_OWNER = 4;

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    /// <summary>ふつうのアプリのウィンドウらしいか（持ち主のいない・ある程度の大きさの・ツールウィンドウでないもの）</summary>
    public static bool IsLikelyAppWindow(IntPtr h)
    {
        if (!IsWindowVisible(h) || GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return false;
        if (IsIconic(h)) return true; // 最小化中は大きさが小さく見えるので、大きさでは判定しない
        return GetWindowRect(h, out var r) && r.Right - r.Left >= 200 && r.Bottom - r.Top >= 120;
    }

    /// <summary>指定したプロセスたちが持つ、アプリのウィンドウ（手前にあるものから順に）</summary>
    public static List<IntPtr> GetAppWindowsOf(HashSet<uint> pids)
    {
        var list = new List<IntPtr>();
        if (pids.Count == 0) return list;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (pids.Contains(pid) && IsLikelyAppWindow(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>いま画面にある（表示中の）トップレベルウィンドウをすべて返す</summary>
    public static HashSet<IntPtr> GetVisibleTopWindows()
    {
        var set = new HashSet<IntPtr>();
        EnumWindows((h, _) => { if (IsWindowVisible(h)) set.Add(h); return true; }, IntPtr.Zero);
        return set;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    public const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    // ---- すりガラス効果 ----
    public const int WCA_ACCENT_POLICY = 19;
    public const int ACCENT_ENABLE_BLURBEHIND = 3;
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor; // AABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    public static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // ---- ごみ箱 ----
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    // ---- アイコン取得 ----
    [Flags]
    public enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        IconOnly = 0x04,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeSize
    {
        public int Width;
        public int Height;
        public NativeSize(int w, int h) { Width = w; Height = h; }
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    public static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfield0;
        public uint dsBitfield1;
        public uint dsBitfield2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    public static extern int GetObject(IntPtr hgdiobj, int cbBuffer, ref DIBSECTION lpvObject);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr hObject);
}
