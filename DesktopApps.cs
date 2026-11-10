using System.IO;

namespace Islandock;

/// <summary>
/// デスクトップにあるアプリ（ショートカット・exe・Steam などの .url）を探す。
/// </summary>
public static class DesktopApps
{
    /// <summary>アプリとして扱う拡張子</summary>
    private static readonly string[] AppExtensions = { ".lnk", ".exe", ".url", ".appref-ms" };

    /// <summary>自分のデスクトップと「全ユーザー共通のデスクトップ」の両方から探す</summary>
    public static List<string> Find()
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),       // OneDrive に移動していても OK
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), // C:\Users\Public\Desktop
        };

        var result = new List<string>();
        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                result.AddRange(Directory.EnumerateFiles(folder)
                    .Where(f => AppExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                // 読めないフォルダは飛ばす
            }
        }
        return result;
    }

    /// <summary>Dock に表示する名前。ファイル名が空（例: ".lnk"）ならリンク先の名前を使う</summary>
    public static string DisplayName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (!string.IsNullOrWhiteSpace(name)) return name;

        string target = IconHelper.ResolveShortcut(path).TrimEnd('\\');
        string targetName = Path.GetFileNameWithoutExtension(target);
        return string.IsNullOrWhiteSpace(targetName) ? Loc.T("アプリ") : targetName;
    }
}
