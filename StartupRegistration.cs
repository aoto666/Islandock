using Microsoft.Win32;

namespace Islandock;

/// <summary>
/// 「PC の起動時に開く」。HKCU\...\Run に登録する（管理者の権限はいらない）。
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Islandock";

    private static string? Command =>
        Environment.ProcessPath is { } exe ? $"\"{exe}\"" : null;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>登録 / 解除する。うまくいかなかったら false</summary>
    public static bool SetEnabled(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
            {
                if (Command is not { } cmd) return false;
                key.SetValue(ValueName, cmd);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            App.WriteCrashLog("スタートアップの登録", ex);
            return false;
        }
    }

    /// <summary>
    /// 初めて起動したときに、「PC の起動時に開く」をオンにする（既定でオン）。
    /// 2回目以降は何もしないので、ユーザーが設定でオフにしたらそのまま。
    /// </summary>
    public static void ApplyDefaultOnce()
    {
        var settings = DockConfig.LoadSettings();
        if (settings.StartupDefaultApplied) return;
        if (SetEnabled(true))
        {
            settings.StartupDefaultApplied = true;
            DockConfig.SaveSettings(settings);
        }
    }

    /// <summary>登録済みなのに、登録した場所の exe がなくなっていたら（フォルダーを移動したなど）、いまの場所に書き直す</summary>
    public static void RefreshPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string current && Command is { } cmd &&
                !System.IO.File.Exists(current.Trim('"')))
                key.SetValue(ValueName, cmd);
        }
        catch
        {
        }
    }
}
