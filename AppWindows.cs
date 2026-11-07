using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using static Islandock.NativeMethods;

namespace Islandock;

/// <summary>
/// どのウィンドウが Dock のどのアプリのものかを覚えておく。
///
/// プロセス名だけでは分からないウィンドウがある:
///  ・PowerShell やコマンド プロンプトは、Windows 11 では「ターミナル」(WindowsTerminal) の中で開く
///  ・ストアアプリは ApplicationFrameHost が枠を描いている
/// そこで、Dock から開いたウィンドウ・Dock に格納したウィンドウを全部記録し（Islandock を再起動しても引き継ぐ）、
/// コンソールのアプリは、ターミナルのウィンドウのタイトルからも探す。
/// </summary>
internal static class AppWindows
{
    private sealed record Owner(string EntryPath, uint Pid);

    private static readonly Dictionary<IntPtr, Owner> Owned = new();
    private static readonly Dictionary<IntPtr, DateTime> MinimizedAt = new();
    private static readonly Dictionary<string, bool> ConsoleCache = new(StringComparer.OrdinalIgnoreCase);
    private static WinEventDelegate? _proc;
    private static IntPtr _hook;

    private static string StorePath => Path.Combine(Path.GetDirectoryName(DockConfig.ConfigPath)!, "windows.json");

    public static void Start()
    {
        Load();
        // どのウィンドウがいつ最小化されたかを記録する（全部しまってあるとき、最後にしまったものから戻すため）
        _proc = (_, _, hwnd, idObject, idChild, _, _) =>
        {
            if (idObject == 0 && idChild == 0 && hwnd != IntPtr.Zero) MinimizedAt[hwnd] = DateTime.Now;
        };
        _hook = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZESTART, IntPtr.Zero, _proc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    public static void Stop()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>そのウィンドウを、この Dock のアプリのものとして覚える</summary>
    public static void Remember(IntPtr hwnd, DockEntry entry)
    {
        if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(entry.Path)) return;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (Owned.TryGetValue(hwnd, out var o) && o.Pid == pid && o.EntryPath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)) return;
        Owned[hwnd] = new Owner(entry.Path, pid);
        Save();
    }

    public static void NoteMinimized(IntPtr hwnd) => MinimizedAt[hwnd] = DateTime.Now;

    public static DateTime LastMinimized(IntPtr hwnd) => MinimizedAt.TryGetValue(hwnd, out var t) ? t : DateTime.MinValue;

    /// <summary>覚えているウィンドウのうち、そのアプリのもので、まだ開いているもの</summary>
    public static IEnumerable<IntPtr> OwnedBy(DockEntry entry)
    {
        Prune();
        return Owned.Where(kv => kv.Value.EntryPath.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList();
    }

    /// <summary>覚えている持ち主（なければ null）</summary>
    public static string? OwnerPath(IntPtr hwnd)
    {
        if (!Owned.TryGetValue(hwnd, out var o)) return null;
        if (!IsAlive(hwnd, o)) return null;
        return o.EntryPath;
    }

    private static bool IsAlive(IntPtr hwnd, Owner o)
    {
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == o.Pid; // 同じ番号が別のウィンドウに使い回されていないか
    }

    private static void Prune()
    {
        var dead = Owned.Where(kv => !IsAlive(kv.Key, kv.Value)).Select(kv => kv.Key).ToList();
        foreach (var h in dead) Owned.Remove(h);
        foreach (var h in MinimizedAt.Keys.Where(h => !IsWindow(h)).ToList()) MinimizedAt.Remove(h);
        if (dead.Count > 0) Save();
    }

    // ===== コンソールのアプリ（ターミナルの中で開くもの） =====

    private static readonly HashSet<string> ConsoleWindowClasses = new() { "CASCADIA_HOSTING_WINDOW_CLASS", "ConsoleWindowClass" };

    /// <summary>コンソールのアプリか（exe のヘッダーの「サブシステム」で判定）</summary>
    public static bool IsConsoleApp(string exePath)
    {
        if (string.IsNullOrEmpty(exePath) || exePath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return false;
        if (ConsoleCache.TryGetValue(exePath, out bool cached)) return cached;
        bool result = false;
        try
        {
            string path = File.Exists(exePath) ? exePath : Path.Combine(Environment.SystemDirectory, Path.GetFileName(exePath));
            if (File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                using var fs = File.OpenRead(path);
                using var br = new BinaryReader(fs);
                fs.Position = 0x3C;
                int pe = br.ReadInt32();
                fs.Position = pe;
                if (br.ReadUInt32() == 0x00004550) // "PE\0\0"
                {
                    fs.Position = pe + 4 + 20 + 68; // オプションヘッダーの Subsystem
                    result = br.ReadUInt16() == 3; // IMAGE_SUBSYSTEM_WINDOWS_CUI
                }
            }
        }
        catch
        {
        }
        ConsoleCache[exePath] = result;
        return result;
    }

    /// <summary>そのアプリのウィンドウのタイトルに含まれそうな言葉（exe 名・ファイルの説明・Dock での名前）</summary>
    private static List<string> TitleKeywords(DockEntry entry, string exePath)
    {
        var words = new List<string>();
        string file = Path.GetFileName(exePath);
        if (file.Length > 0) words.Add(file); // 例: cmd.exe
        try
        {
            string path = File.Exists(exePath) ? exePath : Path.Combine(Environment.SystemDirectory, file);
            if (File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (info.FileDescription is { Length: >= 3 } d) words.Add(d); // 例: Windows PowerShell
            }
        }
        catch
        {
        }
        if (entry.Name.Length >= 3) words.Add(entry.Name);
        return words.Select(Normalize).Where(w => w.Length >= 3).Distinct().ToList();
    }

    private static string Normalize(string s) => s.Replace(" ", "").Replace("　", "").ToLowerInvariant();

    /// <summary>
    /// ターミナル / コンソールのウィンドウのうち、そのアプリが中で動いているもの。
    ///  ・ターミナルのウィンドウには、タブごとに見えない「疑似コンソール」のウィンドウがひも付いていて、
    ///    その持ち主のプロセスが、中で動いているシェル（powershell / cmd など）になっている。これで判断する
    ///  ・それで分からないもの（古い形のコンソールなど）は、タイトルで判断する
    /// </summary>
    public static List<IntPtr> ConsoleWindowsOf(DockEntry entry, string exePath)
    {
        var result = new List<IntPtr>();
        if (!IsConsoleApp(exePath)) return result;
        string shell = Path.GetFileNameWithoutExtension(exePath);
        foreach (var (owner, name) in HostedShells())
            if (name.Equals(shell, StringComparison.OrdinalIgnoreCase) && !result.Contains(owner)) result.Add(owner);
        var words = TitleKeywords(entry, exePath);
        if (words.Count == 0) return result;
        EnumWindows((h, _) =>
        {
            if (!result.Contains(h) && IsLikelyAppWindow(h) && ConsoleWindowClasses.Contains(ClassOf(h)))
            {
                string title = Normalize(TitleOf(h));
                if (words.Any(w => title.Contains(w))) result.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static List<(IntPtr Owner, string Shell)> _hostedCache = new();
    private static DateTime _hostedAt = DateTime.MinValue;

    /// <summary>（ターミナルのウィンドウ, 中で動いているシェルの名前）の一覧。何度も呼ばれるので 1 秒だけ覚えておく</summary>
    private static List<(IntPtr Owner, string Shell)> HostedShells()
    {
        if (DateTime.Now - _hostedAt < TimeSpan.FromSeconds(1)) return _hostedCache;
        var list = new List<(IntPtr, string)>();
        var names = new Dictionary<uint, string>();
        EnumWindows((h, _) =>
        {
            if (ClassOf(h) != "PseudoConsoleWindow") return true;
            IntPtr owner = GetWindow(h, GW_OWNER);
            if (owner == IntPtr.Zero || !IsWindowVisible(owner)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!names.TryGetValue(pid, out var name))
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
                names[pid] = name;
            }
            if (name.Length > 0) list.Add((owner, name));
            return true;
        }, IntPtr.Zero);
        _hostedCache = list;
        _hostedAt = DateTime.Now;
        return list;
    }

    /// <summary>ウィンドウを、画面の手前にあるものから順に並べる</summary>
    public static List<IntPtr> InZOrder(IEnumerable<IntPtr> windows)
    {
        var set = new HashSet<IntPtr>(windows);
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            if (set.Contains(h)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(128);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    // ===== 保存 =====

    private sealed class Stored
    {
        public long Hwnd { get; set; }
        public uint Pid { get; set; }
        public string Entry { get; set; } = "";
    }

    private static void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var list = JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(StorePath)) ?? new();
            foreach (var s in list) Owned[(IntPtr)s.Hwnd] = new Owner(s.Entry, s.Pid);
            Prune();
        }
        catch
        {
        }
    }

    private static void Save()
    {
        try
        {
            var list = Owned.Select(kv => new Stored { Hwnd = (long)kv.Key, Pid = kv.Value.Pid, Entry = kv.Value.EntryPath }).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(list));
        }
        catch
        {
        }
    }

    // ===== Win32 =====

    private delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate proc, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

}
