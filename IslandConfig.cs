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
}

public static class IslandConfig
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Islandock", "island.json");

    public static IslandSettings Load()
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
