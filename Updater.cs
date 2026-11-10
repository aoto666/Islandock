using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace Islandock;

/// <summary>
/// GitHub の最新リリースを見て、新しい版があれば Islandock.exe を入れ替えて再起動する。
///
/// 動いている exe は上書きできないが、名前を変えることはできるので、
/// いまの exe を「Islandock.exe.old」に変えてから新しい exe を置き、次の起動で .old を消す。
/// </summary>
public static class Updater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/aoto666/Islandock/releases/latest";
    private const string ReleasesPage = "https://github.com/aoto666/Islandock/releases/latest";

    /// <summary>新しい exe から起動したときに付ける引数（前の Islandock が終わるのを待ってから起動する）</summary>
    public const string UpdatedArg = "--updated";

    private static bool _busy;

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    private static string? ExePath => Environment.ProcessPath;

    /// <summary>
    /// dotnet build で作った開発用の exe（横に Islandock.dll がある）は、1つの exe にまとまっていないので入れ替えない
    /// </summary>
    private static bool IsDevBuild =>
        ExePath is not { } exe || File.Exists(Path.ChangeExtension(exe, ".dll"));

    private record Release(Version Version, string Tag, string ZipUrl);

    /// <summary>前回の更新で残った古い exe を消す</summary>
    public static void CleanupOld()
    {
        if (ExePath is not { } exe) return;
        try
        {
            File.Delete(exe + ".old");
        }
        catch
        {
            // 前の Islandock がまだ終わりきっていないなど。次の起動で消す
        }
    }

    /// <summary>起動時に、少し待ってから更新を確認する（設定でオフなら何もしない）</summary>
    public static void CheckOnStartup()
    {
        if (!DockConfig.LoadSettings().AutoUpdateCheck || IsDevBuild) return;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            await CheckAsync(manual: false);
        };
        timer.Start();
    }

    /// <summary>
    /// 更新を確認する。manual = 設定画面のボタンから（最新のときや失敗したときもそう伝え、見送った版も聞き直す）
    /// </summary>
    public static async Task CheckAsync(bool manual)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            Release? latest;
            try
            {
                latest = await FetchLatestAsync();
            }
            catch (Exception ex)
            {
                if (manual)
                    MessageBox.Show(Loc.T("アップデートを確認できませんでした。\nインターネットにつながっているか確かめてください。\n\n") + ex.Message,
                        "Islandock", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var settings = DockConfig.LoadSettings();
            if (latest is null || latest.Version <= CurrentVersion)
            {
                if (manual)
                    MessageBox.Show(Loc.F("Islandock は最新です（バージョン {0}）。", CurrentVersion), "Islandock",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!manual && settings.SkippedUpdateVersion == latest.Tag) return;

            if (IsDevBuild)
            {
                MessageBox.Show(Loc.F("新しいバージョン {0} があります。\n開発用のビルドなので、自動では入れ替えません。", latest.Version),
                    "Islandock", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var answer = MessageBox.Show(
                Loc.F("Islandock の新しいバージョン {0} があります（いまは {1}）。\n\n" +
                      "いま更新しますか？ ダウンロードが終わると、Islandock が自動で再起動します。\n\n" +
                      "「いいえ」を選ぶと、このバージョンはもうお知らせしません\n（設定画面の「アップデートを確認」からいつでも更新できます）。",
                      latest.Version, CurrentVersion),
                Loc.T("Islandock のアップデート"), MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
            {
                settings.SkippedUpdateVersion = latest.Tag;
                DockConfig.SaveSettings(settings);
                return;
            }

            await InstallAsync(latest);
        }
        finally
        {
            _busy = false;
        }
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Islandock/{CurrentVersion}");
        return http;
    }

    private static async Task<Release?> FetchLatestAsync()
    {
        using var http = NewClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = new Version(version.Major, version.Minor, Math.Max(version.Build, 0));

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            if (name.EndsWith("-windows-x64.zip", StringComparison.OrdinalIgnoreCase))
                return new Release(version, tag, asset.GetProperty("browser_download_url").GetString()!);
        }
        return null;
    }

    private static async Task InstallAsync(Release release)
    {
        string exe = ExePath!;
        string work = Path.Combine(Path.GetTempPath(), "Islandock-update");
        string zipPath = Path.Combine(work, "update.zip");
        string newExe = Path.Combine(work, "Islandock.exe");
        bool moved = false;

        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);

            using (var http = NewClient())
            await using (var src = await http.GetStreamAsync(release.ZipUrl))
            await using (var dst = File.Create(zipPath))
                await src.CopyToAsync(dst);

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                                e.Name.Equals("Islandock.exe", StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException(Loc.T("ダウンロードした zip に Islandock.exe が入っていません。"));
                entry.ExtractToFile(newExe, overwrite: true);
            }

            // 念のため、ちゃんとした exe か（先頭が MZ か）を確かめる
            using (var f = File.OpenRead(newExe))
            {
                if (f.Length < 1024 * 1024 || f.ReadByte() != 'M' || f.ReadByte() != 'Z')
                    throw new InvalidDataException(Loc.T("ダウンロードした Islandock.exe が壊れています。"));
            }

            string old = exe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(exe, old);
            moved = true;
            File.Copy(newExe, exe);

            var settings = DockConfig.LoadSettings();
            settings.SkippedUpdateVersion = null;
            DockConfig.SaveSettings(settings);

            // 後片付け（タスクバーを戻す・二重起動防止の印を手放す）が終わってから、新しい exe を起動する
            Application.Current.Exit += (_, _) =>
                Process.Start(new ProcessStartInfo(exe, UpdatedArg) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("アップデート", ex);
            if (moved && !File.Exists(exe))
            {
                // 新しい exe を置けなかったら、元に戻す
                try { File.Move(exe + ".old", exe); } catch { }
            }
            var answer = MessageBox.Show(
                Loc.T("アップデートできませんでした。\n\n") + ex.Message +
                Loc.T("\n\nダウンロードページを開きますか？（zip をダウンロードして、いまの Islandock.exe と入れ替えてください）"),
                "Islandock", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(ReleasesPage) { UseShellExecute = true });
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
        }
    }
}
