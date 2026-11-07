using System.IO;
using System.Text.Json;

namespace Islandock;

/// <summary>Dock に並べるアプリ1つ分の情報</summary>
public class DockEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>起動中かどうかを調べるときのプロセス名（ストアアプリなど、パスから分からないとき用）</summary>
    public string? ProcessName { get; set; }

    /// <summary>ストアアプリなど「shell:AppsFolder\…」形式で登録されたアプリか</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsShellApp => Path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Dock から起動したときに出てきたウィンドウ（保存はしない）。
    /// プロセス名から探せないアプリ（ストアアプリ・.url・ランチャー経由など）でも、もう一度押したらしまえるようにする。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IntPtr LaunchedWindow { get; set; }
}

/// <summary>見た目などの設定</summary>
public class DockSettings
{
    /// <summary>アイコンを macOS 風のタイルに揃える</summary>
    public bool IconTiles { get; set; } = true;

    /// <summary>マウスを近づけたときに拡大する</summary>
    public bool Magnification { get; set; } = true;

    /// <summary>マウスを画面の下端に当てたときだけ Dock を表示する</summary>
    public bool AutoHide { get; set; } = true;

    /// <summary>アプリを開いたとき、ウィンドウをアイコンから引き伸ばして出す（ジニーエフェクト）</summary>
    public bool OpenAnimation { get; set; } = true;

    /// <summary>デスクトップに置かれたアプリを自動で Dock に追加する</summary>
    public bool AutoAddDesktopApps { get; set; } = true;

    /// <summary>Dock の右端にごみ箱（と区切り線）を表示する</summary>
    public bool ShowTrash { get; set; } = true;

    /// <summary>
    /// 一度でも自動追加の対象として見たデスクトップのファイル。
    /// ユーザーが Dock から削除したものを、また勝手に追加しないために覚えておく。
    /// </summary>
    public List<string> SeenDesktopItems { get; set; } = new();
}

/// <summary>
/// Dock の設定を %APPDATA%\Islandock\ に保存・読み込みする。
/// </summary>
public static class DockConfig
{
    public static string SettingsPath => System.IO.Path.Combine(ConfigDir, "settings.json");

    public static DockSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<DockSettings>(File.ReadAllText(SettingsPath)) ?? new DockSettings();
        }
        catch
        {
        }
        return new DockSettings();
    }

    public static void SaveSettings(DockSettings settings)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static readonly string ConfigDir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Islandock");

    public static readonly string ConfigPath = System.IO.Path.Combine(ConfigDir, "dock.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語をそのまま保存
    };

    public static List<DockEntry> Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var list = JsonSerializer.Deserialize<List<DockEntry>>(File.ReadAllText(ConfigPath));
                if (list != null) return list;
            }
        }
        catch
        {
            // 設定ファイルが壊れていたら初期設定に戻す
        }

        var defaults = CreateDefaults();
        Save(defaults);
        return defaults;
    }

    public static void Save(List<DockEntry> entries)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(entries, JsonOptions));
    }

    /// <summary>初回起動時に並べるアプリ（PCに存在するものだけ）</summary>
    private static List<DockEntry> CreateDefaults()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var candidates = new List<DockEntry>
        {
            new() { Name = "エクスプローラー", Path = System.IO.Path.Combine(win, "explorer.exe") },
            new() { Name = "Microsoft Edge", Path = System.IO.Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe") },
            new() { Name = "メモ帳", Path = System.IO.Path.Combine(sys, "notepad.exe") },
            new() { Name = "電卓", Path = System.IO.Path.Combine(sys, "calc.exe") },
            new() { Name = "コマンドプロンプト", Path = System.IO.Path.Combine(sys, "cmd.exe") },
        };
        return candidates.Where(c => File.Exists(c.Path)).ToList();
    }
}
