using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace Islandock;

/// <summary>
/// macOS の「ジニーエフェクト」。Dock のアイコンからウィンドウが引き伸ばされるように出てくる。
///
/// 仕組み:
///  1. 開いたアプリのウィンドウを一瞬だけ透明にする
///  2. ウィンドウの中身を画像として取り込む
///  3. その画像を、細い帯をたくさん並べた「ゴム板」（3D メッシュ）に貼り、
///     アイコン → ウィンドウの位置へ、漏斗のような形を通って伸びていくように動かす
///  4. 動き終わったら本物のウィンドウを元に戻す
/// </summary>
public static class Genie
{
    private static readonly HashSet<IntPtr> Running = new();
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(520);

    // ===================== 起動したアプリの新しいウィンドウを待つ =====================

    private static WinEventDelegate? _hookProc; // ガベージコレクションで消されないよう保持
    private static IntPtr _hook;
    private static Action<IntPtr>? _onFound;
    private static string _watchName = "";
    private static string? _watchHost;
    private static HashSet<IntPtr> _ignore = new();
    private static System.Windows.Threading.DispatcherTimer? _watchTimeout;

    /// <summary>
    /// 指定したプロセス名の「新しいウィンドウ」が画面に出た瞬間に onFound を呼ぶ。
    /// 出た瞬間に呼ぶことで、ちらつく前にウィンドウを隠せる。
    /// </summary>
    /// <param name="hostName">そのアプリのウィンドウを代わりに出すプロセス（PowerShell なら WindowsTerminal）</param>
    public static void WatchForNewWindow(string processName, HashSet<IntPtr> existing, Action<IntPtr> onFound, TimeSpan timeout,
        string? hostName = null)
    {
        StopWatching();
        if (string.IsNullOrEmpty(processName)) return;

        _watchName = processName;
        _watchHost = hostName;
        _ignore = existing;
        _onFound = onFound;
        _hookProc = OnWinEvent;
        // 作られた瞬間（まだ画面に出る前）に隠しておき、表示されたらアニメーションで出す
        _hook = SetWinEventHook(EVENT_OBJECT_CREATE, EVENT_OBJECT_SHOW, IntPtr.Zero, _hookProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        _watchTimeout = new System.Windows.Threading.DispatcherTimer { Interval = timeout };
        _watchTimeout.Tick += (_, _) => StopWatching();
        _watchTimeout.Start();
    }

    private static void StopWatching()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _onFound = null;
        _watchTimeout?.Stop();
        _watchTimeout = null;
        // 隠したまま出てこなかったウィンドウは元に戻す
        foreach (var h in PreHidden.Keys.ToList()) ReleasePreHidden(h);
        _pidNames.Clear();
    }

    /// <summary>作られた瞬間に隠したウィンドウ（表示されたらアニメーションで出す）</summary>
    private static readonly Dictionary<IntPtr, WindowHider> PreHidden = new();
    /// <summary>隠した状態のまま Open に引き渡すウィンドウ</summary>
    private static readonly Dictionary<IntPtr, WindowHider> HandOff = new();
    private static readonly Dictionary<uint, string> _pidNames = new();

    private static void ReleasePreHidden(IntPtr hwnd)
    {
        if (!PreHidden.Remove(hwnd, out var hider)) return;
        hider.Restore();
        if (IsWindow(hwnd)) SetTransitions(hwnd, enabled: true);
    }

    private static bool IsWatchedProcess(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (!_pidNames.TryGetValue(pid, out var name))
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
            }
            catch
            {
                name = "";
            }
            _pidNames[pid] = name;
        }
        return name.Equals(_watchName, StringComparison.OrdinalIgnoreCase) ||
               (_watchHost != null && name.Equals(_watchHost, StringComparison.OrdinalIgnoreCase));
    }

    private static void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero || _onFound == null) return;
        if (_ignore.Contains(hwnd)) return;

        if (evt == EVENT_OBJECT_DESTROY)
        {
            PreHidden.Remove(hwnd);
            return;
        }

        if (evt == EVENT_OBJECT_CREATE)
        {
            // 起動したアプリの、持ち主のいないトップレベルのウィンドウなら、画面に出る前に隠しておく
            if (PreHidden.ContainsKey(hwnd) || GetAncestor(hwnd, GA_ROOT) != hwnd || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return;
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0 || !IsWatchedProcess(hwnd)) return;
            bool transitionsOff = SetTransitions(hwnd, enabled: false);
            var hider = WindowHider.Hide(hwnd, allowRegion: true);
            if (hider != null) PreHidden[hwnd] = hider;
            else if (transitionsOff) SetTransitions(hwnd, enabled: true);
            return;
        }

        // EVENT_OBJECT_SHOW
        if (!IsWatchedProcess(hwnd)) return;
        if (!IsAppWindow(hwnd, allowDirectComposition: true))
        {
            // Edge などは、最初は小さく作ってから大きくする。隠してあるウィンドウは、大きくなるのを少し待つ
            if (PreHidden.ContainsKey(hwnd)) WaitForSize(hwnd);
            return;
        }
        Found(hwnd);
    }

    private static void Found(IntPtr hwnd)
    {
        if (_onFound == null) return;
        if (PreHidden.Remove(hwnd, out var pre)) HandOff[hwnd] = pre;
        var found = _onFound;
        StopWatching();
        found(hwnd);
    }

    private static void WaitForSize(IntPtr hwnd)
    {
        var started = DateTime.Now;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            if (!PreHidden.ContainsKey(hwnd) || _onFound == null)
            {
                timer.Stop();
                return;
            }
            if (IsAppWindow(hwnd, allowDirectComposition: true))
            {
                timer.Stop();
                Found(hwnd);
            }
            else if (!IsWindow(hwnd) || DateTime.Now - started > TimeSpan.FromSeconds(1.5))
            {
                timer.Stop();
                ReleasePreHidden(hwnd); // 小さなポップアップなどだった
            }
        };
        timer.Start();
    }

    /// <summary>
    /// ふつうのアプリのウィンドウか（小さなポップアップや通知などは除く）。
    /// allowDirectComposition: Chrome などの「透明にできない描き方」のウィンドウも含める（しまう・戻すとき用）
    /// </summary>
    private static bool IsAppWindow(IntPtr hwnd, bool allowDirectComposition = false)
    {
        if (GetAncestor(hwnd, GA_ROOT) != hwnd || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        if (!IsWindowVisible(hwnd)) return false;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        if (!allowDirectComposition && (ex & WS_EX_NOREDIRECTIONBITMAP) != 0) return false;
        if (!GetWindowRect(hwnd, out var r) || r.Right - r.Left < 320 || r.Bottom - r.Top < 200) return false;

        // ストアアプリの枠は別のプロセスが描いているので、取り込み・透明化ができない
        var cls = new System.Text.StringBuilder(64);
        GetClassName(hwnd, cls, cls.Capacity);
        return cls.ToString() is not ("ApplicationFrameWindow" or "Windows.UI.Core.CoreWindow");
    }

    // ===================== アニメーション本体 =====================

    /// <summary>
    /// ウィンドウを、アイコンの位置から引き伸ばして出現させる。
    /// </summary>
    /// <param name="hwnd">対象のウィンドウ</param>
    /// <param name="iconPx">Dock のアイコンの位置（画面の物理ピクセル）</param>
    /// <param name="dpiScale">画面の拡大率（1.5 = 150%）</param>
    public static async void Open(IntPtr hwnd, Rect iconPx, double dpiScale)
    {
        bool iconic = IsIconic(hwnd);
        HandOff.Remove(hwnd, out var pre);
        if (pre == null && !IsAppWindow(hwnd, allowDirectComposition: true) && !iconic) return;
        if (!Running.Add(hwnd))
        {
            pre?.Restore();
            return;
        }

        // 起動したばかりのウィンドウは、作られた瞬間に隠してある（pre）
        // 透明にできないウィンドウ（Chrome やターミナル）は、表示範囲を空にして隠す
        var hider = pre ?? WindowHider.Hide(hwnd, allowRegion: !iconic);
        bool transitionsOff = pre != null || (!iconic && hider != null && SetTransitions(hwnd, enabled: false));
        if (hider == null)
        {
            // 透明にできないウィンドウ（Chrome など）は、しまったときの画像を使って戻す
            try
            {
                if (iconic) await RestoreFromSnapshot(hwnd, iconPx, dpiScale);
            }
            finally
            {
                Running.Remove(hwnd);
            }
            return;
        }

        GenieWindow? overlay = null;
        try
        {
            if (IsIconic(hwnd))
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }

            // アプリが中身を描き終わるのを少し待つ
            await Task.Delay(200);
            if (!IsWindow(hwnd)) return;

            var (image, framePx) = Capture(hwnd);
            if (image == null) return;
            // 表示範囲を空にして隠したウィンドウは、中身が写らないことがある（真っ黒になる）
            if (hider.ByRegion && IsBlank(image)) image = Placeholder(framePx, dark: GetClassNameOf(hwnd) is "CASCADIA_HOSTING_WINDOW_CLASS" or "ConsoleWindowClass");

            var frame = new Rect(framePx.X / dpiScale, framePx.Y / dpiScale, framePx.Width / dpiScale, framePx.Height / dpiScale);
            var icon = new Rect(iconPx.X / dpiScale, iconPx.Y / dpiScale, iconPx.Width / dpiScale, iconPx.Height / dpiScale);

            overlay = new GenieWindow(image, frame, icon);
            overlay.Show();
            await overlay.PlayAsync(Duration);

            // 最後のコマは本物と同じ位置・大きさなので、入れ替えても気づかない
            hider.Restore();
            await Task.Delay(40);
        }
        catch
        {
        }
        finally
        {
            hider.Restore();
            overlay?.Close();
            if (transitionsOff && IsWindow(hwnd)) SetTransitions(hwnd, enabled: true);
            Running.Remove(hwnd);
        }
    }

    /// <summary>
    /// 開くときの逆再生で、ウィンドウをアイコンへ吸い込ませてから最小化する。
    /// </summary>
    public static async void Minimize(IntPtr hwnd, Rect iconPx, double dpiScale)
    {
        if (!IsAppWindow(hwnd, allowDirectComposition: true) || IsIconic(hwnd) || iconPx.IsEmpty)
        {
            ShowWindow(hwnd, SW_MINIMIZE);
            return;
        }
        if (!Running.Add(hwnd)) return;

        WindowHider? hider = null;
        GenieWindow? overlay = null;
        bool minimized = false;
        bool transitionsOff = false;
        try
        {
            // 見えている今のうちに中身を取り込む（戻すときにも使うので覚えておく）
            var (image, framePx) = Capture(hwnd);
            if (image == null)
            {
                ShowWindow(hwnd, SW_MINIMIZE);
                minimized = true;
                return;
            }
            ForgetClosedWindows();
            Snapshots[hwnd] = new Snapshot(image, framePx);

            // まず本物とまったく同じ位置に画像を重ねてから、本物を消す（入れ替えに気づかない）
            overlay = new GenieWindow(image, ToDip(framePx, dpiScale), ToDip(iconPx, dpiScale), startAtIcon: false);
            overlay.Show();
            await Task.Delay(30);

            hider = WindowHider.Hide(hwnd);
            if (hider == null)
            {
                // 透明にできないウィンドウ（Chrome など）は、画像で覆っているうちに先に最小化してしまう。
                // Windows 標準の最小化アニメーションは止めておく
                transitionsOff = SetTransitions(hwnd, enabled: false);
                ShowWindow(hwnd, SW_MINIMIZE);
                minimized = true;
            }

            await overlay.PlayAsync(Duration, reverse: true);

            // 透明なまま最小化するので、Windows 標準の最小化アニメーションは見えない
            if (!minimized)
            {
                ShowWindow(hwnd, SW_MINIMIZE);
                minimized = true;
            }
            overlay.Close();
            overlay = null;
            await Task.Delay(350);
        }
        catch
        {
            if (!minimized) ShowWindow(hwnd, SW_MINIMIZE);
        }
        finally
        {
            hider?.Restore();
            overlay?.Close();
            if (transitionsOff) SetTransitions(hwnd, enabled: true);
            Running.Remove(hwnd);
        }
    }

    // ===================== タイトルバーの最小化ボタンで、しまうとき =====================
    //
    // 他のアプリの「最小化ボタンが押された」ことを直接知る手段はないので、
    //  1. マウスのボタンが押された瞬間に、カーソルの下が最小化ボタンかを調べる
    //  2. そうなら、見えている今のうちに中身を取り込み、Windows 標準の最小化アニメーションを止めておく
    //  3. 実際に最小化が始まったら（EVENT_SYSTEM_MINIMIZESTART）、取り込んだ画像を Dock へ吸い込ませる

    private static WinEventDelegate? _minimizeHookProc;
    private static IntPtr _minimizeHook;
    private static System.Windows.Threading.DispatcherTimer? _mousePoll;
    private static bool _mouseWasDown;
    private static Func<IntPtr, bool>? _isDockApp;
    private static Func<IntPtr, Rect>? _revealIcon;
    private static Func<double>? _dpiScale;

    /// <summary>最小化ボタンが押され、最小化が始まるのを待っているウィンドウ</summary>
    private static readonly Dictionary<IntPtr, Snapshot> PendingMinimize = new();

    /// <summary>
    /// タイトルバーの最小化ボタンでしまうときにも、ジニーエフェクトを使う。
    /// isDockApp: そのウィンドウが Dock にあるアプリのものか（アニメーション設定がオフなら false を返す）
    /// revealIcon: Dock を出して、そのアプリのアイコンの位置（物理ピクセル）を返す
    /// </summary>
    public static void WatchTitleBarMinimize(Func<IntPtr, bool> isDockApp, Func<IntPtr, Rect> revealIcon, Func<double> dpiScale)
    {
        _isDockApp = isDockApp;
        _revealIcon = revealIcon;
        _dpiScale = dpiScale;

        _minimizeHookProc = OnMinimizeStart;
        _minimizeHook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero, _minimizeHookProc,
            0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        _mousePoll = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        _mousePoll.Tick += (_, _) => PollMouse();
        _mousePoll.Start();
    }

    public static void StopWatchingTitleBarMinimize()
    {
        _mousePoll?.Stop();
        _mousePoll = null;
        if (_minimizeHook != IntPtr.Zero) UnhookWinEvent(_minimizeHook);
        _minimizeHook = IntPtr.Zero;
        foreach (var h in PendingMinimize.Keys.ToList()) CancelPending(h);
    }

    private static void PollMouse()
    {
        int vk = GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON;
        bool down = (GetAsyncKeyState(vk) & 0x8000) != 0;
        bool pressed = down && !_mouseWasDown;
        _mouseWasDown = down;
        if (!pressed || !GetCursorPos(out var pt)) return;

        IntPtr hwnd = GetAncestor(WindowFromPoint(pt), GA_ROOT);
        if (hwnd == IntPtr.Zero || Running.Contains(hwnd) || PendingMinimize.ContainsKey(hwnd)) return;

        IntPtr lParam = (IntPtr)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF));
        if (SendMessageTimeout(hwnd, WM_NCHITTEST, IntPtr.Zero, lParam, SMTO_ABORTIFHUNG, 50, out IntPtr hit) == IntPtr.Zero) return;
        if ((int)hit == HTCLOSE)
        {
            if (AnimationsEnabled?.Invoke() == true) BeginClose(hwnd, () => (GetAsyncKeyState(vk) & 0x8000) != 0, TimeSpan.FromMilliseconds(400));
            return;
        }
        if ((int)hit != HTMINBUTTON) return;

        if (IsIconic(hwnd) || !IsAppWindow(hwnd, allowDirectComposition: true)) return;
        if (_isDockApp?.Invoke(hwnd) != true) return;

        var (image, framePx) = Capture(hwnd);
        if (image == null || !SetTransitions(hwnd, enabled: false)) return;
        PendingMinimize[hwnd] = new Snapshot(image, framePx);

        // ボタンの外で離すなどして最小化されなかったら、標準のアニメーションを元に戻す
        var timeout = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) { timeout.Start(); return; } // まだ押している
            CancelPending(hwnd);
        };
        timeout.Start();
    }

    private static void CancelPending(IntPtr hwnd)
    {
        if (PendingMinimize.Remove(hwnd) && IsWindow(hwnd)) SetTransitions(hwnd, enabled: true);
    }

    private static void OnMinimizeStart(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || !PendingMinimize.Remove(hwnd, out var snap)) return;
        PlayTitleBarMinimize(hwnd, snap);
    }

    private static async void PlayTitleBarMinimize(IntPtr hwnd, Snapshot snap)
    {
        if (!Running.Add(hwnd))
        {
            SetTransitions(hwnd, enabled: true);
            return;
        }

        GenieWindow? overlay = null;
        try
        {
            var iconPx = _revealIcon?.Invoke(hwnd) ?? Rect.Empty;
            if (iconPx.IsEmpty) return;
            double dpi = _dpiScale?.Invoke() ?? 1;

            // Dock から戻すときにも使えるよう覚えておく（透明にできないウィンドウ用）
            ForgetClosedWindows();
            Snapshots[hwnd] = snap;

            overlay = new GenieWindow(snap.Image, ToDip(snap.FramePx, dpi), ToDip(iconPx, dpi), startAtIcon: false);
            overlay.Show();
            await overlay.PlayAsync(Duration, reverse: true);
        }
        catch
        {
        }
        finally
        {
            overlay?.Close();
            SetTransitions(hwnd, enabled: true);
            Running.Remove(hwnd);
        }
    }

    // ===================== 閉じるとき =====================

    /// <summary>アニメーションの設定がオンか（Dock 側から渡す）</summary>
    public static Func<bool>? AnimationsEnabled { get; set; }

    private static readonly HashSet<IntPtr> PendingClose = new();

    /// <summary>
    /// Dock の「終了」から: ウィンドウを閉じて、縮みながら消えるアニメーションを出す。
    /// 保存の確認などで閉じなかったら、何もしない。
    /// </summary>
    public static void CloseWithAnimation(IntPtr hwnd)
    {
        if (AnimationsEnabled?.Invoke() == true && !IsIconic(hwnd))
            BeginClose(hwnd, () => false, TimeSpan.FromMilliseconds(1500));
        PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>開発用: 閉じるアニメーションの経過（確認用）</summary>
    public static Action<string>? CloseTrace { get; set; }

    /// <summary>
    /// 見えている今のうちに中身を写し取り、まったく同じ位置に重ねておく。
    /// ウィンドウが消えたら、写し取った画像を縮めながら消す。
    /// stillPressing: まだボタンを押している間は待つ / wait: 押し終わってからこの時間内に閉じなければ取りやめ
    /// </summary>
    private static void BeginClose(IntPtr hwnd, Func<bool> stillPressing, TimeSpan wait)
    {
        if (PendingClose.Contains(hwnd) || Running.Contains(hwnd)) return;
        if (IsIconic(hwnd) || !IsAppWindow(hwnd, allowDirectComposition: true)) { CloseTrace?.Invoke("skip: not app window"); return; }
        var (image, framePx) = Capture(hwnd);
        if (image == null) { CloseTrace?.Invoke("skip: capture failed"); return; }
        CloseTrace?.Invoke($"overlay shown {framePx}");

        double dpi = _dpiScale?.Invoke() ?? 1;
        var overlay = new CloseWindow(image, ToDip(framePx, dpi));
        overlay.Show();
        bool transitionsOff = SetTransitions(hwnd, enabled: false); // Windows 標準の閉じるアニメーションは止める
        PendingClose.Add(hwnd);

        DateTime? releasedAt = null;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            if (!IsWindow(hwnd) || !IsWindowVisible(hwnd))
            {
                timer.Stop();
                PendingClose.Remove(hwnd);
                CloseTrace?.Invoke("window closed -> play");
                overlay.PlayClose();
                if (transitionsOff && IsWindow(hwnd)) SetTransitions(hwnd, enabled: true);
                return;
            }
            if (stillPressing()) return;
            releasedAt ??= DateTime.Now;
            if (DateTime.Now - releasedAt < wait) return;

            // 閉じなかった（ボタンの外で離した・保存の確認が出た など）
            timer.Stop();
            PendingClose.Remove(hwnd);
            overlay.Close();
            if (transitionsOff) SetTransitions(hwnd, enabled: true);
        };
        timer.Start();
    }

    /// <summary>閉じたウィンドウの画像を、少し縮めながら消す</summary>
    private sealed class CloseWindow : Window
    {
        private readonly ScaleTransform _scale = new(1, 1);

        public CloseWindow(BitmapSource image, Rect frame)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
            Left = frame.X;
            Top = frame.Y;
            Width = frame.Width;
            Height = frame.Height;
            var img = new System.Windows.Controls.Image { Source = image, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            Content = new System.Windows.Controls.Grid
            {
                Children = { img },
                RenderTransform = _scale,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        public void PlayClose()
        {
            var duration = TimeSpan.FromMilliseconds(230);
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn };
            var shrink = new System.Windows.Media.Animation.DoubleAnimation(0.86, duration) { EasingFunction = ease };
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, duration) { EasingFunction = ease };
            fade.Completed += (_, _) => Close();
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
            BeginAnimation(OpacityProperty, fade);
        }
    }

    // ===================== 透明にできないウィンドウ用 =====================

    private sealed record Snapshot(BitmapSource Image, Rect FramePx);

    /// <summary>しまったときのウィンドウの画像と位置（最小化中は中身を取り込めないので）</summary>
    private static readonly Dictionary<IntPtr, Snapshot> Snapshots = new();

    private static void ForgetClosedWindows()
    {
        foreach (var h in Snapshots.Keys.Where(h => !IsWindow(h)).ToList()) Snapshots.Remove(h);
    }

    private static Rect ToDip(Rect px, double dpiScale) =>
        new(px.X / dpiScale, px.Y / dpiScale, px.Width / dpiScale, px.Height / dpiScale);

    /// <summary>
    /// しまったときの画像を、アイコンからウィンドウの位置まで引き伸ばし、
    /// 動き終わった瞬間に本物を（Windows 標準のアニメーションなしで）元に戻す。
    /// </summary>
    private static async Task RestoreFromSnapshot(IntPtr hwnd, Rect iconPx, double dpiScale)
    {
        if (!Snapshots.Remove(hwnd, out var snap) || iconPx.IsEmpty)
        {
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            return;
        }

        GenieWindow? overlay = null;
        bool transitionsOff = false;
        bool restored = false;
        try
        {
            overlay = new GenieWindow(snap.Image, ToDip(snap.FramePx, dpiScale), ToDip(iconPx, dpiScale));
            overlay.Show();
            await overlay.PlayAsync(Duration);

            transitionsOff = SetTransitions(hwnd, enabled: false);
            ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            restored = true;
            await Task.Delay(80); // 本物が描き終わるのを待ってから画像を外す
        }
        catch
        {
            if (!restored)
            {
                ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
        }
        finally
        {
            overlay?.Close();
            if (transitionsOff) SetTransitions(hwnd, enabled: true);
        }
    }

    /// <summary>画像がほぼ真っ黒（または透明）か</summary>
    private static bool IsBlank(BitmapSource image)
    {
        var src = image.Format == PixelFormats.Bgra32 || image.Format == PixelFormats.Bgr32
            ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        int stride = w * 4;
        var row = new byte[stride];
        for (int k = 1; k <= 12; k++)
        {
            int y = h * k / 13;
            src.CopyPixels(new Int32Rect(0, y, w, 1), row, stride, 0);
            for (int x = 0; x < w; x += Math.Max(1, w / 40))
            {
                int i = x * 4;
                if (row[i] > 12 || row[i + 1] > 12 || row[i + 2] > 12) return false;
            }
        }
        return true;
    }

    /// <summary>中身が写せなかったときの、ウィンドウの形をした代わりの絵</summary>
    private static BitmapSource Placeholder(Rect framePx, bool dark)
    {
        int w = Math.Max(1, (int)(framePx.Width / 2)), h = Math.Max(1, (int)(framePx.Height / 2));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var body = new SolidColorBrush(dark ? Color.FromRgb(0x0C, 0x0C, 0x0C) : Color.FromRgb(0xF7, 0xF7, 0xF8));
            var bar = new SolidColorBrush(dark ? Color.FromRgb(0x20, 0x20, 0x22) : Color.FromRgb(0xEC, 0xEC, 0xEE));
            dc.DrawRectangle(body, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(bar, null, new Rect(0, 0, w, Math.Min(18, h)));
        }
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    private static string GetClassNameOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(128);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Windows 標準の最小化・復元アニメーションのオン/オフ。変更できたら true</summary>
    private static bool SetTransitions(IntPtr hwnd, bool enabled)
    {
        int disabled = enabled ? 0 : 1;
        return DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disabled, sizeof(int)) == 0;
    }

    /// <summary>ウィンドウの中身を画像にする（影などの見えない枠は除く）</summary>
    private static (BitmapSource?, Rect) Capture(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var wr)) return (null, Rect.Empty);
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT fr, Marshal.SizeOf<RECT>()) != 0) fr = wr;

        int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
        if (w <= 0 || h <= 0) return (null, Rect.Empty);

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            if (!PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT)) return (null, Rect.Empty);
            SelectObject(mem, old);

            var crop = new Int32Rect(
                Math.Clamp(fr.Left - wr.Left, 0, w - 1), Math.Clamp(fr.Top - wr.Top, 0, h - 1),
                Math.Max(1, Math.Min(fr.Right - fr.Left, w)), Math.Max(1, Math.Min(fr.Bottom - fr.Top, h)));
            if (crop.X + crop.Width > w) crop.Width = w - crop.X;
            if (crop.Y + crop.Height > h) crop.Height = h - crop.Y;

            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, crop, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return (src, new Rect(wr.Left + crop.X, wr.Top + crop.Y, crop.Width, crop.Height));
        }
        finally
        {
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>他のアプリのウィンドウを一時的に透明にして、あとで元に戻す</summary>
    private sealed class WindowHider
    {
        private readonly IntPtr _hwnd;
        private readonly bool _wasLayered;
        private readonly byte _alpha;
        private readonly bool _byRegion;
        private bool _restored;

        /// <summary>表示範囲を空にして隠したか（このときは中身を写し取れないことがある）</summary>
        public bool ByRegion => _byRegion;

        private WindowHider(IntPtr hwnd, bool wasLayered, byte alpha, bool byRegion = false)
        {
            _hwnd = hwnd;
            _wasLayered = wasLayered;
            _alpha = alpha;
            _byRegion = byRegion;
        }

        /// <param name="allowRegion">透明にできないウィンドウは、表示範囲を空にして隠す</param>
        public static WindowHider? Hide(IntPtr hwnd, bool allowRegion = false)
        {
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            // Chrome などは透明化の指定が成功しても実際には消えないので、透明化は使わない
            if ((ex & WS_EX_NOREDIRECTIONBITMAP) != 0) return allowRegion ? HideByRegion(hwnd) : null;
            bool layered = (ex & WS_EX_LAYERED) != 0;
            byte alpha = 255;
            if (layered)
            {
                // すでに独自の透明処理をしているウィンドウは、壊さないよう透明化では触らない
                if (!GetLayeredWindowAttributes(hwnd, out _, out alpha, out uint flags) || (flags & LWA_ALPHA) == 0)
                    return allowRegion ? HideByRegion(hwnd) : null;
            }
            else
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED);
            }
            if (!SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA))
            {
                if (!layered) SetWindowLong(hwnd, GWL_EXSTYLE, ex);
                return allowRegion ? HideByRegion(hwnd) : null;
            }
            return new WindowHider(hwnd, layered, alpha);
        }

        private static WindowHider? HideByRegion(IntPtr hwnd)
        {
            IntPtr rgn = CreateRectRgn(0, 0, 0, 0);
            if (SetWindowRgn(hwnd, rgn, true) == 0)
            {
                DeleteObject(rgn);
                return null;
            }
            return new WindowHider(hwnd, false, 255, byRegion: true); // 成功したら領域は Windows が持つ
        }

        public void Restore()
        {
            if (_restored) return;
            _restored = true;
            if (!IsWindow(_hwnd)) return;
            if (_byRegion)
            {
                SetWindowRgn(_hwnd, IntPtr.Zero, true);
                return;
            }
            SetLayeredWindowAttributes(_hwnd, 0, _alpha, LWA_ALPHA);
            if (!_wasLayered) SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) & ~WS_EX_LAYERED);
        }
    }

    /// <summary>アニメーションを描く、マウスを素通りさせる透明なウィンドウ</summary>
    private sealed class GenieWindow : Window
    {
        private const int Rows = 72; // 帯の数（多いほど曲線がなめらか）
        private readonly MeshGeometry3D _mesh = new();
        private readonly Rect _frame, _icon; // このウィンドウ内の座標
        private readonly double _height;

        public GenieWindow(BitmapSource image, Rect frame, Rect icon, bool startAtIcon = true)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;

            // 動く範囲だけを覆う（画面全体を覆うより軽い）
            var bounds = Rect.Union(frame, icon);
            bounds.Inflate(2, 2);
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
            _height = bounds.Height;
            _frame = new Rect(frame.X - bounds.X, frame.Y - bounds.Y, frame.Width, frame.Height);
            _icon = new Rect(icon.X - bounds.X, icon.Y - bounds.Y, icon.Width, icon.Height);

            // 帯の並び（左右2点 × 行数）と、画像のどこを貼るか
            var tex = new PointCollection();
            var idx = new Int32Collection();
            for (int i = 0; i <= Rows; i++)
            {
                double v = (double)i / Rows;
                tex.Add(new Point(0, v));
                tex.Add(new Point(1, v));
                if (i < Rows)
                {
                    int a = i * 2;
                    idx.Add(a); idx.Add(a + 2); idx.Add(a + 1);
                    idx.Add(a + 1); idx.Add(a + 2); idx.Add(a + 3);
                }
            }
            _mesh.TextureCoordinates = tex;
            _mesh.TriangleIndices = idx;
            Update(startAtIcon ? 1 : 0);

            var brush = new ImageBrush(image) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1) };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            var material = new DiffuseMaterial(brush);
            var model = new GeometryModel3D(_mesh, material) { BackMaterial = material };

            var group = new Model3DGroup();
            group.Children.Add(new AmbientLight(Colors.White));
            group.Children.Add(model);

            // 画面の座標（左上が 0,0、下向きが +Y）と 3D の座標を一致させるカメラ
            var viewport = new Viewport3D
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Camera = new OrthographicCamera(new Point3D(bounds.Width / 2, -bounds.Height / 2, 10),
                    new Vector3D(0, 0, -1), new Vector3D(0, 1, 0), bounds.Width),
                ClipToBounds = false,
            };
            viewport.Children.Add(new ModelVisual3D { Content = group });
            Content = new Grid { Children = { viewport } };
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        }

        /// <summary>アイコン(1) → ウィンドウ(0) へ動かす。reverse ならウィンドウ → アイコン（逆再生）</summary>
        public Task PlayAsync(TimeSpan duration, bool reverse = false)
        {
            var done = new TaskCompletionSource();
            var clock = Stopwatch.StartNew();
            void OnFrame(object? s, EventArgs e)
            {
                double t = Math.Min(1, clock.Elapsed.TotalSeconds / duration.TotalSeconds);
                Update(reverse ? t : 1 - t);
                if (t >= 1)
                {
                    CompositionTarget.Rendering -= OnFrame;
                    done.TrySetResult();
                }
            }
            CompositionTarget.Rendering += OnFrame;
            return done.Task;
        }

        private static double Smooth(double x)
        {
            x = Math.Clamp(x, 0, 1);
            return x * x * (3 - 2 * x);
        }

        /// <summary>
        /// p = 0: 元のウィンドウの形 / p = 1: アイコンに吸い込まれた状態。
        /// 前半で左右の辺が漏斗の形に曲がり、後半で中身が漏斗を通ってアイコンへ滑り込む。
        /// </summary>
        private void Update(double p)
        {
            double bend = Smooth(p / 0.45);          // 辺の曲がり具合
            double slide = Smooth((p - 0.2) / 0.8);  // 中身の滑り込み具合

            double top = _frame.Top, bottom = _frame.Bottom;
            double funnelEnd = Math.Max(_icon.Bottom, top + 1); // 漏斗の出口（アイコンの下端）
            double travel = funnelEnd - top;

            var pts = new Point3DCollection((Rows + 1) * 2);
            for (int i = 0; i <= Rows; i++)
            {
                double v = (double)i / Rows;
                double y = top + v * (bottom - top) + slide * travel;
                if (y > funnelEnd) y = funnelEnd;

                // 高さ y における漏斗の幅（上はウィンドウの幅、下はアイコンの幅）
                double e = Smooth((y - top) / travel);
                double funnelL = _frame.Left + (_icon.Left - _frame.Left) * e;
                double funnelR = _frame.Right + (_icon.Right - _frame.Right) * e;
                double xl = _frame.Left + (funnelL - _frame.Left) * bend;
                double xr = _frame.Right + (funnelR - _frame.Right) * bend;

                pts.Add(new Point3D(xl, -y, 0));
                pts.Add(new Point3D(xr, -y, 0));
            }
            _mesh.Positions = pts;
        }
    }

    // ===================== Windows API =====================

    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x200000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x2;
    private const uint GA_ROOT = 2;
    private const uint GW_OWNER = 4;
    private const int SW_RESTORE = 9;
    private const int SW_MINIMIZE = 6;
    private const uint PW_RENDERFULLCONTENT = 2;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const int HTCLOSE = 20;
    private const uint WM_CLOSE = 0x0010;
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    private const uint WM_NCHITTEST = 0x0084;
    private const int HTMINBUTTON = 8;
    private const uint SMTO_ABORTIFHUNG = 0x2;
    private const int SM_SWAPBUTTON = 23;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_TRANSITIONS_FORCEDISABLED = 3;
}
