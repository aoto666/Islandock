namespace Islandock;

/// <summary>アプリ一覧の1件</summary>
public sealed class AppInfo
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public string? ProcessName { get; init; }
    public required string Source { get; init; }
}

/// <summary>
/// PC に入っているアプリを集める。
/// Windows の「すべてのアプリ」（shell:AppsFolder）を使うので、
/// スタートメニューのアプリもストアアプリ（Claude など）も両方とれる。
/// </summary>
public static class InstalledApps
{
    public static List<AppInfo> FindAll()
    {
        var result = new List<AppInfo>();

        // 1. デスクトップのアイコン
        foreach (var file in DesktopApps.Find())
            result.Add(new AppInfo { Name = DesktopApps.DisplayName(file), Path = file, Source = "デスクトップ" });

        // 2. すべてのアプリ
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type != null)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                dynamic items = folder.Items();
                int count = items.Count;
                for (int i = 0; i < count; i++)
                {
                    dynamic item = items.Item(i);
                    string name = item.Name;
                    string id = item.Path;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    if (id.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue; // Web へのリンクは除外
                    if (name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) || name.Contains("アンインストール")) continue;

                    // exe が分かるなら、起動中の判定用にプロセス名を覚える
                    string? process = id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? System.IO.Path.GetFileNameWithoutExtension(id)
                        : null;
                    result.Add(new AppInfo { Name = name, Path = @"shell:AppsFolder\" + id, ProcessName = process, Source = "アプリ" });
                }
            }
        }
        catch
        {
            // 取得できなくても、デスクトップの分だけで続ける
        }

        // 同じ名前が重なったら、デスクトップのものを優先して1つにする
        return result
            .GroupBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }
}
