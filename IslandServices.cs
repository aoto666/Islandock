using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Islandock;

// ===================================================================
//  Dynamic Island が使う「見張り役」たち
//  ・MediaMonitor   … 再生中の曲（Spotify / ブラウザ / メディアプレーヤーなど）
//  ・AudioMonitor   … 音量・消音・出力先（イヤホンをつないだ等）
//  ・PowerMonitor   … 充電の開始/終了・バッテリー残量・省電力モード
//  ・PrivacyMonitor … マイク / カメラを使っているアプリ
// ===================================================================

/// <summary>再生中の曲の情報</summary>
public sealed class MediaInfo
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string AppName { get; init; } = "";
    public BitmapSource? Art { get; set; }
    public Color Accent { get; set; } = Colors.White;
    public bool IsPlaying { get; set; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime PositionAt { get; init; }
    public bool CanSeek { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrevious { get; init; }

    /// <summary>いまの再生位置（最後に知らされた位置から経過時間を足して推定）</summary>
    public TimeSpan CurrentPosition
    {
        get
        {
            if (!IsPlaying || Duration <= TimeSpan.Zero) return Position;
            var p = Position + (DateTime.Now - PositionAt);
            return p > Duration ? Duration : p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }
}

/// <summary>Windows の「メディアの再生情報」を使って、再生中の曲を見張る</summary>
public sealed class MediaMonitor
{
    private readonly Dispatcher _dispatcher;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _artKey = "";
    private bool _refreshing, _pending;

    public MediaInfo? Current { get; private set; }
    public event Action? Changed;

    /// <summary>開発用: 架空の曲を「再生中」にする（紹介動画の書き出し用）</summary>
    internal void SetDemo(MediaInfo info)
    {
        Current = info;
        Changed?.Invoke();
    }

    public MediaMonitor(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public async void Start()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _dispatcher.InvokeAsync(Refresh);
            _manager.SessionsChanged += (_, _) => _dispatcher.InvokeAsync(Refresh);
            Refresh();
        }
        catch
        {
            // この PC で使えない場合は、音楽の表示だけ出ない
        }
    }

    /// <summary>最新の状態を読み直す（定期的にも呼ばれる）</summary>
    public async void Refresh()
    {
        if (_manager == null) return;
        if (_refreshing) { _pending = true; return; }
        _refreshing = true;
        try
        {
            do
            {
                _pending = false;
                await RefreshCoreAsync();
            } while (_pending);
        }
        catch
        {
        }
        finally
        {
            _refreshing = false;
        }
    }

    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        // 再生中のものを優先。なければ Windows が「現在」とするもの
        var sessions = _manager!.GetSessions();
        foreach (var s in sessions)
        {
            if (s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                return s;
        }
        return _manager.GetCurrentSession() ?? sessions.FirstOrDefault();
    }

    private async Task RefreshCoreAsync()
    {
        var session = PickSession();
        if (session == null)
        {
            _session = null;
            Publish(null);
            return;
        }

        var props = await session.TryGetMediaPropertiesAsync();
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        if (props == null || string.IsNullOrWhiteSpace(props.Title))
        {
            _session = session;
            Publish(null);
            return;
        }

        var updated = timeline.LastUpdatedTime.LocalDateTime;
        if (updated > DateTime.Now || updated.Year < 2000) updated = DateTime.Now;
        var duration = timeline.EndTime - timeline.StartTime;

        var info = new MediaInfo
        {
            Title = props.Title,
            Artist = props.Artist ?? "",
            AppName = FriendlyAppName(session.SourceAppUserModelId),
            IsPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            Position = timeline.Position - timeline.StartTime,
            Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
            PositionAt = updated,
            CanSeek = playback.Controls.IsPlaybackPositionEnabled && duration > TimeSpan.Zero,
            CanNext = playback.Controls.IsNextEnabled,
            CanPrevious = playback.Controls.IsPreviousEnabled,
        };

        // アルバムアートは曲が変わったときだけ読み込む
        string key = session.SourceAppUserModelId + "|" + props.Title + "|" + props.Artist;
        if (key == _artKey && Current?.Art != null)
        {
            info.Art = Current.Art;
            info.Accent = Current.Accent;
        }
        else if (props.Thumbnail != null)
        {
            try
            {
                (info.Art, info.Accent) = await LoadArtAsync(props.Thumbnail);
                _artKey = key;
            }
            catch
            {
            }
        }

        _session = session;
        Publish(info);
    }

    private void Publish(MediaInfo? info) => _dispatcher.Invoke(() =>
    {
        Current = info;
        Changed?.Invoke();
    });

    // ----- 操作 -----

    public void PlayPause()
    {
        if (Current != null) { Current.IsPlaying = !Current.IsPlaying; Changed?.Invoke(); } // 先に見た目を変える
        Run(s => s.TryTogglePlayPauseAsync().AsTask());
    }

    public void Next() => Run(s => s.TrySkipNextAsync().AsTask());
    public void Previous() => Run(s => s.TrySkipPreviousAsync().AsTask());
    public void Seek(TimeSpan position) => Run(s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask());

    private async void Run(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
    {
        if (_session == null) return;
        try { await command(_session); } catch { }
        await Task.Delay(250);
        Refresh();
    }

    // ----- 補助 -----

    private static string FriendlyAppName(string id)
    {
        // 例: "Spotify.exe" / "MSEdge" / "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"
        if (string.IsNullOrEmpty(id)) return "";
        string s = id;
        int bang = s.IndexOf('!');
        if (bang >= 0) s = s[..bang];
        int us = s.IndexOf('_');
        if (us > 0) s = s[..us];
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        int dot = s.LastIndexOf('.');
        if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];
        return s switch
        {
            "MSEdge" or "msedge" => "Microsoft Edge",
            "chrome" or "Chrome" => "Google Chrome",
            "firefox" => "Firefox",
            "ZuneMusic" => Loc.T("メディア プレーヤー"),
            "SpotifyMusic" => "Spotify",
            _ => s,
        };
    }

    private static async Task<(BitmapSource, Color)> LoadArtAsync(IRandomAccessStreamReference reference)
    {
        using var stream = await reference.OpenReadAsync();
        using var input = stream.AsStreamForRead();
        var ms = new MemoryStream();
        await input.CopyToAsync(ms);
        ms.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 200;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return (bmp, AccentFrom(bmp));
    }

    /// <summary>アルバムアートから「波形の色」にする鮮やかな色を取り出す</summary>
    private static Color AccentFrom(BitmapSource bmp)
    {
        try
        {
            var small = new TransformedBitmap(new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0),
                new ScaleTransform(16.0 / bmp.PixelWidth, 16.0 / bmp.PixelHeight));
            int w = small.PixelWidth, h = small.PixelHeight;
            var px = new byte[w * h * 4];
            small.CopyPixels(px, w * 4, 0);

            double r = 0, g = 0, b = 0, sum = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                int max = Math.Max(px[i], Math.Max(px[i + 1], px[i + 2]));
                int min = Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
                double sat = (max - min) / 255.0;
                double weight = 0.03 + sat * sat * (max / 255.0); // 鮮やかで明るい色を重視
                b += px[i] * weight; g += px[i + 1] * weight; r += px[i + 2] * weight;
                sum += weight;
            }
            r /= sum; g /= sum; b /= sum;

            // 黒い背景の上で見えるよう明るくする
            double top = Math.Max(r, Math.Max(g, b));
            double scale = top < 1 ? 1 : Math.Max(1, 215 / top);
            return Color.FromRgb((byte)Math.Min(255, r * scale), (byte)Math.Min(255, g * scale), (byte)Math.Min(255, b * scale));
        }
        catch
        {
            return Colors.White;
        }
    }
}

/// <summary>音量・消音・出力先デバイスを見張る（Windows のオーディオ API を直接使う）</summary>
public sealed class AudioMonitor
{
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _volume;
    private string? _deviceId;
    private bool _hasReadOnce;
    private int _tick;
    private DateTime _retryAt = DateTime.MinValue;

    public float Level { get; private set; }
    public bool Muted { get; private set; }
    public string DeviceName { get; private set; } = "";
    public bool Available => _volume != null;

    /// <summary>音量か消音が変わった（引数: 消音が変わったか）</summary>
    public event Action<bool>? VolumeChanged;
    /// <summary>音の出力先が変わった（イヤホン・スピーカーの切り替えなど）</summary>
    public event Action? DeviceChanged;

    /// <summary>短い間隔で呼ぶ</summary>
    public void Poll()
    {
        if (DateTime.Now < _retryAt) return;
        try
        {
            _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (_volume == null || _tick++ % 6 == 0) CheckDevice();
            if (_volume == null) return;

            _volume.GetMasterVolumeLevelScalar(out float level);
            _volume.GetMute(out bool muted);
            bool levelChanged = Math.Abs(level - Level) > 0.004;
            bool muteChanged = muted != Muted;
            Level = level;
            Muted = muted;
            if (levelChanged || muteChanged) VolumeChanged?.Invoke(muteChanged);
        }
        catch
        {
            _volume = null;
            _deviceId = null;
            _retryAt = DateTime.Now.AddSeconds(3); // 音声デバイスがないときは少し待ってから再挑戦
        }
    }

    private void CheckDevice()
    {
        _enumerator!.GetDefaultAudioEndpoint(0 /* 再生 */, 1 /* マルチメディア */, out var device);
        device.GetId(out string id);
        if (id == _deviceId && _volume != null) return;

        _deviceId = id;
        var iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object endpoint);
        _volume = (IAudioEndpointVolume)endpoint;
        _volume.GetMasterVolumeLevelScalar(out float level);
        _volume.GetMute(out bool muted);
        Level = level;
        Muted = muted;
        DeviceName = ReadFriendlyName(device);

        if (_hasReadOnce) DeviceChanged?.Invoke(); // 起動直後は知らせない
        _hasReadOnce = true;
    }

    public void SetLevel(double level)
    {
        if (_volume == null) return;
        var ctx = Guid.Empty;
        try
        {
            _volume.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), ref ctx);
            Level = (float)level;
            if (Muted && level > 0) { _volume.SetMute(false, ref ctx); Muted = false; }
        }
        catch { }
    }

    public void ToggleMute()
    {
        if (_volume == null) return;
        var ctx = Guid.Empty;
        try { _volume.SetMute(!Muted, ref ctx); } catch { }
    }

    private static string ReadFriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(0, out var store);
            var key = new PropertyKey { FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), PropertyId = 14 };
            store.GetValue(ref key, out var value);
            string name = value.Type == 31 /* 文字列 */ ? Marshal.PtrToStringUni(value.Pointer) ?? "" : "";
            PropVariantClear(ref value);
            return name;
        }
        catch
        {
            return "";
        }
    }

    // ----- Windows のオーディオ API（Core Audio）の宣言 -----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
        void OpenPropertyStore(int access, out IPropertyStore store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        void GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out int count);
        void GetAt(int index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Type;
        public ushort Reserved1, Reserved2, Reserved3;
        public IntPtr Pointer;
        public IntPtr Pointer2;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}

/// <summary>充電・バッテリー残量・省電力モードを見張る</summary>
public sealed class PowerMonitor
{
    public enum Change { PluggedIn, Unplugged, Low, SaverOn, SaverOff }

    private bool _initialized, _ac, _saver;
    private int _percent;

    public bool HasBattery { get; private set; }
    public bool OnAc => _ac;
    public int Percent => _percent;
    public event Action<Change>? Changed;

    public void Poll()
    {
        if (!GetSystemPowerStatus(out var s)) return;
        bool hasBattery = s.BatteryFlag != 128 && s.BatteryLifePercent <= 100;
        bool ac = s.ACLineStatus == 1;
        bool saver = s.SystemStatusFlag == 1;
        int percent = hasBattery ? s.BatteryLifePercent : 100;

        if (_initialized && hasBattery)
        {
            if (ac && !_ac) Changed?.Invoke(Change.PluggedIn);
            if (!ac && _ac) Changed?.Invoke(Change.Unplugged);
            if (!ac && ((percent <= 20 && _percent > 20) || (percent <= 10 && _percent > 10)))
            {
                _percent = percent;
                Changed?.Invoke(Change.Low);
            }
        }
        if (_initialized && saver != _saver) Changed?.Invoke(saver ? Change.SaverOn : Change.SaverOff);

        HasBattery = hasBattery;
        _ac = ac;
        _saver = saver;
        _percent = percent;
        _initialized = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}

/// <summary>マイク・カメラを使っているアプリを見張る（Windows のプライバシー設定の記録を読む）</summary>
public sealed class PrivacyMonitor
{
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";
    private bool _initialized;

    public List<string> MicApps { get; private set; } = new();
    public List<string> CameraApps { get; private set; } = new();

    /// <summary>使い始めたアプリがあった（引数: カメラか, アプリ名）</summary>
    public event Action<bool, string>? Started;
    public event Action? Changed;

    public void Poll()
    {
        var mic = AppsUsing("microphone");
        var cam = AppsUsing("webcam");
        if (_initialized)
        {
            foreach (var app in cam.Except(CameraApps)) Started?.Invoke(true, app);
            foreach (var app in mic.Except(MicApps)) Started?.Invoke(false, app);
        }
        bool changed = !mic.SequenceEqual(MicApps) || !cam.SequenceEqual(CameraApps);
        MicApps = mic;
        CameraApps = cam;
        _initialized = true;
        if (changed) Changed?.Invoke();
    }

    private static List<string> AppsUsing(string capability)
    {
        var result = new List<string>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(Root + capability);
            if (root == null) return result;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                if (key == null) continue;
                if (name == "NonPackaged")
                {
                    // 普通のアプリ（キー名は exe のパスの \ を # にしたもの）
                    foreach (var exe in key.GetSubKeyNames())
                    {
                        using var sub = key.OpenSubKey(exe);
                        if (sub != null && InUse(sub))
                            result.Add(Path.GetFileNameWithoutExtension(exe.Replace('#', '\\')));
                    }
                }
                else if (InUse(key))
                {
                    // ストアアプリ（例: Microsoft.WindowsCamera_8wekyb3d8bbwe）
                    string s = name.Split('_')[0];
                    result.Add(s[(s.LastIndexOf('.') + 1)..]);
                }
            }
        }
        catch
        {
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result.Distinct().ToList();
    }

    private static bool InUse(RegistryKey key) =>
        key.GetValue("LastUsedTimeStop") is long stop && stop == 0 &&
        key.GetValue("LastUsedTimeStart") is long start && start > 0;
}
