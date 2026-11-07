using System.IO;
using System.Text.Json;

namespace Islandock;

/// <summary>Dynamic Island の設定（%APPDATA%\Islandock\island.json）</summary>
public class IslandSettings
{
    /// <summary>
    /// 「BGM を再生」で開く YouTube の再生リスト（URL か list= の ID）。最初は空で、アイランドの「再生中」から設定する。
    /// v= 付きの URL にすると、その動画から再生リストが流れる。
    /// </summary>
    public string BgmPlaylist { get; set; } = "";

    /// <summary>アイランドそのものを使う</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>「再生中」（音楽の表示と操作）</summary>
    public bool ShowMedia { get; set; } = true;

    /// <summary>「タイマー / ストップウォッチ」</summary>
    public bool ShowTimer { get; set; } = true;

    /// <summary>「カレンダー」</summary>
    public bool ShowCalendar { get; set; } = true;

    /// <summary>「ファイル共有」（近距離共有・スマホから受け取る）</summary>
    public bool ShowFileShare { get; set; } = true;

    /// <summary>音量・充電・出力先などのお知らせ</summary>
    public bool ShowAlerts { get; set; } = true;

    /// <summary>マイク / カメラを使っている間のランプ</summary>
    public bool ShowPrivacy { get; set; } = true;

    /// <summary>
    /// このバージョンが知らない項目（別のバージョンが書いたものなど）。保存するときに消さずにそのまま残す。
    /// </summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public static class IslandConfig
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Islandock", "island.json");

    private static IslandSettings? _shared;

    /// <summary>設定を返す。アプリ全体で同じものを使う</summary>
    public static IslandSettings Load() => _shared ??= LoadFromFile();

    private static IslandSettings LoadFromFile()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<IslandSettings>(File.ReadAllText(ConfigPath)) ?? new IslandSettings();
        }
        catch
        {
        }
        return new IslandSettings();
    }

    public static void Save(IslandSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(settings, Options));
    }
}
