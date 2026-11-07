using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Islandock;

/// <summary>
/// Dynamic Island の「ライブアクティビティ」部分。
///  ・音楽の再生中: アルバムアートと波形 → 開くと操作パネル（再生位置・前後・音量）
///  ・タイマー / ストップウォッチ: 残り時間を表示し、終わったらアラーム
///  ・お知らせ: 充電・バッテリー低下・省電力・消音・音量・出力先・マイク/カメラ
///  ・同時に2つ動いているときは、2つ目を右の小さな丸に表示（分割表示）
///  ・マイク / カメラを使っている間は、右にオレンジ / 緑のランプ
/// </summary>
public partial class IslandWindow
{
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets";
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
    private static readonly Color Yellow = Color.FromRgb(0xFF, 0xD6, 0x0A);
    private static readonly Brush EmptyArt = Frozen(Color.FromRgb(0x2C, 0x2C, 0x2E));
    private const double MediaCompactWidth = 270;

    private enum Activity { Timer, Stopwatch, Media }
    private enum Mode { Compact, Alert, Expanded }

    private sealed record Alert(string Kind, double Width, double Height, bool Sticky);

    private MediaMonitor _media = null!;
    private readonly AudioMonitor _audio = new();
    private readonly PowerMonitor _power = new();
    private readonly PrivacyMonitor _privacy = new();
    private readonly DispatcherTimer _fast = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _alertTimer = new();
    private readonly DispatcherTimer _alarm = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Random _random = new();

    private readonly IslandBar _progressBar = new(5);
    private readonly IslandBar _volumeBar = new(6);
    private readonly IslandBar _timerBar = new(5, Color.FromRgb(0xFF, 0x9F, 0x0A)) { IsHitTestVisible = false };
    private readonly IslandBar _hudBar = new(5) { IsHitTestVisible = false, Width = 150 };
    private readonly List<Rectangle> _compactBars = new();
    private readonly List<Rectangle> _mediaBars = new();

    private Mode _mode = Mode.Compact;
    private Size _targetSize = new(CompactWidth, CompactHeight);
    private Alert? _alert;
    private DateTime _lastPlayingAt = DateTime.MinValue;
    private BitmapSourceRef _artRef = new(null, null);
    private string _bubbleKey = "";

    // タイマー
    private TimeSpan _timerDuration = TimeSpan.FromMinutes(5);
    private TimeSpan _timerLeft = TimeSpan.FromMinutes(5);
    private DateTime? _timerEndsAt;
    private bool _timerStarted;
    private DateTime _alarmStarted;

    // ストップウォッチ
    private readonly Stopwatch _stopwatch = new();
    private readonly List<TimeSpan> _laps = new();

    /// <summary>アルバムアートの画像と、それから作った塗り（毎回作り直さないため）</summary>
    private sealed record BitmapSourceRef(System.Windows.Media.Imaging.BitmapSource? Source, Brush? Brush);

    private void InitLive()
    {
        MediaProgressHost.Child = _progressBar;
        VolumeHost.Child = _volumeBar;
        TimerProgressHost.Child = _timerBar;
        BuildWave(CompactWave, _compactBars, 4, 3, 2.5);
        BuildWave(MediaWave, _mediaBars, 5, 4, 3);

        foreach (int minutes in new[] { 1, 3, 5, 10, 15, 30 })
        {
            var b = new Button
            {
                Content = $"{minutes}分",
                Style = (Style)FindResource("PillButton"),
                Margin = new Thickness(0, 0, 5, 5),
                Padding = new Thickness(10, 4, 10, 4),
            };
            b.Click += (_, _) => SetTimer(TimeSpan.FromMinutes(minutes));
            TimerPresets.Children.Add(b);
        }

        _progressBar.UserCommitted += v =>
        {
            if (_media.Current is { CanSeek: true } m) _media.Seek(TimeSpan.FromTicks((long)(m.Duration.Ticks * v)));
        };
        _volumeBar.UserChanged += v => _audio.SetLevel(v);

        _media = new MediaMonitor(Dispatcher);
        _media.Changed += OnMediaChanged;
        _media.Start();

        _audio.VolumeChanged += OnVolumeChanged;
        _audio.DeviceChanged += OnOutputDeviceChanged;
        _power.Changed += OnPowerChanged;
        _privacy.Started += OnPrivacyStarted;
        _privacy.Changed += UpdateSideItems;

        _audio.Poll();
        _power.Poll();
        _privacy.Poll();

        _fast.Tick += (_, _) => { _audio.Poll(); UpdateLive(); };
        _slow.Tick += (_, _) => { _power.Poll(); _privacy.Poll(); _media.Refresh(); };
        _alertTimer.Tick += (_, _) => DismissAlert();
        _alarm.Tick += (_, _) =>
        {
            // 1分鳴っても止められなければ自動で止める
            if (DateTime.Now - _alarmStarted > TimeSpan.FromMinutes(1)) StopAlarm();
            else SystemSounds.Exclamation.Play();
        };
        _fast.Start();
        _slow.Start();
        UpdateTimerButtons();
        UpdateStopwatchButtons();
    }

    // ===================== 状態（小さい / お知らせ / 大きい） =====================

    private List<Activity> Activities()
    {
        var list = new List<Activity>();
        if (_timerStarted) list.Add(Activity.Timer);
        if (_stopwatch.IsRunning || _stopwatch.Elapsed > TimeSpan.Zero) list.Add(Activity.Stopwatch);
        if (MediaVisible) list.Add(Activity.Media);
        return list;
    }

    /// <summary>再生中か、一時停止してから 30 秒以内なら表示する</summary>
    private bool MediaVisible => _media?.Current != null &&
                                 (_media.Current.IsPlaying || DateTime.Now - _lastPlayingAt < TimeSpan.FromSeconds(30));

    private static Tab TabOf(Activity a) => a switch
    {
        Activity.Timer or Activity.Stopwatch => Tab.Timer,
        _ => Tab.Media,
    };

    private Tab? MainTab()
    {
        var acts = Activities();
        return acts.Count > 0 ? TabOf(acts[0]) : null;
    }

    private Tab? SecondaryTab()
    {
        var acts = Activities();
        return acts.Count > 1 ? TabOf(acts[1]) : null;
    }

    /// <summary>いまの状態に合わせて、大きさ・表示する中身を切り替える</summary>
    private void ApplyState()
    {
        var mode = _alert?.Sticky == true ? Mode.Alert
            : _expanded ? Mode.Expanded
            : _alert != null ? Mode.Alert
            : Mode.Compact;

        var size = mode switch
        {
            Mode.Expanded => new Size(ExpandedWidth, ExpandedHeight),
            Mode.Alert => new Size(_alert!.Width, _alert.Height),
            _ => CompactSize(),
        };

        if (size != _targetSize)
        {
            bool growing = size.Width * size.Height > _targetSize.Width * _targetSize.Height;
            _targetSize = size;
            // ばねのように少し行き過ぎてから止まる
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = growing ? 0.3 : 0.15 };
            AnimateSize(size.Width, size.Height, mode == Mode.Expanded ? 0.55 : 0.45, ease);
        }

        if (mode != _mode)
        {
            _mode = mode;
            SetViewActive(CompactView, mode == Mode.Compact);
            SetViewActive(AlertView, mode == Mode.Alert);
            SetViewActive(ExpandedView, mode == Mode.Expanded);
        }
        UpdateSideItems();
        UpdateReveal();
    }

    private FrameworkElement ViewOf(Mode mode) => mode switch
    {
        Mode.Expanded => ExpandedView,
        Mode.Alert => AlertView,
        _ => CompactView,
    };

    private void SetViewActive(FrameworkElement view, bool active)
    {
        if (active)
        {
            view.Visibility = Visibility.Visible;
            Fade(view, 1, 0.28, 0.14);
        }
        else
        {
            Fade(view, 0, 0.12, 0, () =>
            {
                if (ViewOf(_mode) != view) view.Visibility = Visibility.Collapsed;
            });
        }
    }

    private Size CompactSize()
    {
        var acts = Activities();
        var main = acts.Count > 0 ? acts[0] : (Activity?)null;
        return new Size(main == Activity.Media ? MediaCompactWidth : CompactWidth, CompactHeight);
    }

    /// <summary>小さいときの中身（時計 / 音楽 / タイマー）を選ぶ</summary>
    private void UpdateCompact()
    {
        if (_media == null) return; // 起動途中
        var acts = Activities();
        var main = acts.Count > 0 ? acts[0] : (Activity?)null;
        CompactIdle.Visibility = main is null ? Visibility.Visible : Visibility.Collapsed;
        CompactMedia.Visibility = main == Activity.Media ? Visibility.Visible : Visibility.Collapsed;
        CompactTimer.Visibility = main is Activity.Timer or Activity.Stopwatch ? Visibility.Visible : Visibility.Collapsed;
        ApplyState();
    }

    /// <summary>右側の小さな丸（2つ目の動作中のもの）とマイク/カメラのランプ</summary>
    private void UpdateSideItems()
    {
        var acts = Activities();
        bool showBubble = _mode == Mode.Compact && acts.Count >= 2;
        if (showBubble)
        {
            var second = acts[1];
            string key = second + (second == Activity.Media ? _artRef.Source?.GetHashCode().ToString() : "");
            if (key != _bubbleKey)
            {
                _bubbleKey = key;
                BubbleContent.Content = MinimalView(second);
            }
        }
        Bubble.Visibility = showBubble ? Visibility.Visible : Visibility.Collapsed;

        bool mic = _privacy.MicApps.Count > 0, cam = _privacy.CameraApps.Count > 0;
        MicDot.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        CameraDot.Visibility = cam ? Visibility.Visible : Visibility.Collapsed;
        PrivacyDots.Visibility = mic || cam ? Visibility.Visible : Visibility.Collapsed;
        PrivacyDots.ToolTip = string.Join("\n",
            _privacy.CameraApps.Select(a => "カメラ: " + a).Concat(_privacy.MicApps.Select(a => "マイク: " + a)));

        // 右側に何か出ていても、アイランド本体は画面の真ん中に保つ
        double right = (showBubble ? 43 : 0) + (mic || cam ? 14 : 0);
        IslandRow.Margin = new Thickness(right, 6, 0, 0);
    }

    private FrameworkElement MinimalView(Activity a)
    {
        switch (a)
        {
            case Activity.Media:
                return new Border
                {
                    Width = 24,
                    Height = 24,
                    CornerRadius = new CornerRadius(12),
                    Background = _artRef.Brush ?? EmptyArt,
                };
            default:
                return Glyph(a == Activity.Timer ? "" : "", 15, Orange);
        }
    }

    // ===================== 毎フレーム（0.1 秒ごと）の更新 =====================

    private void UpdateLive()
    {
        if (_media == null) return;

        // タイマーが終わった？
        if (_timerEndsAt is DateTime end && DateTime.Now >= end) TimerFinished();

        var acts = Activities();
        var main = acts.Count > 0 ? acts[0] : (Activity?)null;
        bool playing = _media.Current?.IsPlaying == true;

        if (_mode == Mode.Compact)
        {
            if (main == Activity.Media) AnimateWave(_compactBars, playing, 18);
            if (main == Activity.Timer)
            {
                CompactTimerIcon.Text = "";
                CompactTimerText.Text = FormatTimer(TimerRemaining);
            }
            else if (main == Activity.Stopwatch)
            {
                CompactTimerIcon.Text = "";
                CompactTimerText.Text = FormatClock(_stopwatch.Elapsed);
            }
            // 状態が変わった（音楽が止まって 30 秒たった等）ときに中身を切り替える
            bool shouldShowMedia = main == Activity.Media;
            if ((CompactMedia.Visibility == Visibility.Visible) != shouldShowMedia ||
                (CompactTimer.Visibility == Visibility.Visible) != (main is Activity.Timer or Activity.Stopwatch))
                UpdateCompact();
        }

        if (_mode != Mode.Expanded) return;

        if (_tab == Tab.Media && _media.Current is { } m)
        {
            AnimateWave(_mediaBars, m.IsPlaying, 24);
            var pos = m.CurrentPosition;
            if (m.Duration > TimeSpan.Zero)
            {
                if (_progressBar.IsDragging) pos = TimeSpan.FromTicks((long)(m.Duration.Ticks * _progressBar.Value));
                else _progressBar.Value = pos.TotalSeconds / m.Duration.TotalSeconds;
                MediaElapsed.Text = FormatClock(pos);
                MediaRemaining.Text = "-" + FormatClock(m.Duration - pos);
            }
            else
            {
                _progressBar.Value = 0;
                MediaElapsed.Text = MediaRemaining.Text = "";
            }
        }
        if (_tab == Tab.Media)
        {
            if (!_volumeBar.IsDragging) _volumeBar.Value = _audio.Muted ? 0 : _audio.Level;
            MuteButton.Content = VolumeGlyph();
            MuteButton.IsEnabled = _audio.Available;
            _volumeBar.IsEnabled = _audio.Available;
        }
        if (_tab == Tab.Timer)
        {
            var left = TimerRemaining;
            TimerText.Text = FormatTimer(left);
            TimerText.Foreground = _timerStarted ? new SolidColorBrush(Orange) : Brushes.White;
            _timerBar.Value = _timerDuration.TotalSeconds > 0 ? left.TotalSeconds / _timerDuration.TotalSeconds : 0;
            StopwatchText.Text = FormatStopwatch(_stopwatch.Elapsed);
        }
    }

    // ===================== 音楽 =====================

    private void OnMediaChanged()
    {
        var m = _media.Current;
        if (m?.IsPlaying == true) _lastPlayingAt = DateTime.Now;

        if (m?.Art != _artRef.Source)
            _artRef = new BitmapSourceRef(m?.Art, m?.Art == null ? null : new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill });
        var art = _artRef.Brush ?? EmptyArt;
        var accent = new SolidColorBrush(m?.Accent ?? Colors.White);

        CompactArt.Background = art;
        CompactPlayButton.Content = m?.IsPlaying == true ? "" : ""; // 一時停止 / 再生
        CompactTitle.Text = m == null ? "" : string.IsNullOrEmpty(m.Artist) ? m.Title : $"{m.Title} · {m.Artist}";
        foreach (var bar in _compactBars.Concat(_mediaBars)) bar.Fill = accent;

        MediaContent.Visibility = m != null ? Visibility.Visible : Visibility.Collapsed;
        MediaEmpty.Visibility = m == null ? Visibility.Visible : Visibility.Collapsed;
        if (m != null)
        {
            MediaArt.Background = art;
            MediaTitle.Text = m.Title;
            MediaArtist.Text = m.Artist;
            MediaApp.Text = m.AppName;
            PlayButton.Content = m.IsPlaying ? "" : "";
            PrevButton.IsEnabled = m.CanPrevious;
            NextButton.IsEnabled = m.CanNext;
            _progressBar.IsHitTestVisible = m.CanSeek;
        }
        UpdateCompact();
    }

    private void OnMediaPlayPause(object sender, RoutedEventArgs e)
    {
        if (_media.Current != null) _media.PlayPause();
        else OpenBgmPlaylist();
    }

    private void OnPlayBgm(object sender, RoutedEventArgs e)
    {
        if (!OpenBgmPlaylist()) ShowBgmEditor(true);
    }

    // ===================== BGM の再生リストの設定 =====================

    /// <summary>再生リストが設定されているかに合わせて、ボタンの表示を変える</summary>
    private void UpdateBgmButtons()
    {
        bool has = BgmUrl(IslandConfig.Load().BgmPlaylist) != null;
        BgmPlayButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        BgmEditButton.Content = has ? "変更" : "♪  BGM の再生リストを設定";
        BgmEditButton.Margin = new Thickness(has ? 6 : 0, 0, 0, 0);
    }

    private void ShowBgmEditor(bool show)
    {
        BgmEditor.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BgmButtons.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        BgmEditorHint.Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93));
        BgmEditorHint.Text = "例: https://www.youtube.com/playlist?list=PL…（空にして保存すると解除）";
        if (show)
        {
            BgmUrlBox.Text = IslandConfig.Load().BgmPlaylist ?? "";
            Activate();
            Dispatcher.BeginInvoke(() => { BgmUrlBox.Focus(); Keyboard.Focus(BgmUrlBox); BgmUrlBox.SelectAll(); },
                System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            UpdateBgmButtons();
        }
    }

    private void OnEditBgm(object sender, RoutedEventArgs e) => ShowBgmEditor(true);
    private void OnCancelBgm(object sender, RoutedEventArgs e) => ShowBgmEditor(false);

    private void OnBgmUrlChanged(object sender, TextChangedEventArgs e) =>
        BgmPlaceholder.Visibility = BgmUrlBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnBgmUrlKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; OnSaveBgm(sender, e); }
        else if (e.Key == Key.Escape) { e.Handled = true; ShowBgmEditor(false); }
    }

    private void OnSaveBgm(object sender, RoutedEventArgs e)
    {
        string value = BgmUrlBox.Text.Trim();
        if (value.Length > 0 && BgmUrl(value) == null)
        {
            BgmEditorHint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x9A));
            BgmEditorHint.Text = "YouTube の再生リスト（または動画）の URL を貼り付けてください";
            return;
        }
        var settings = IslandConfig.Load();
        settings.BgmPlaylist = value;
        IslandConfig.Save(settings);
        ShowBgmEditor(false);
    }

    /// <summary>
    /// 設定に書かれた値（URL か 再生リストの ID）から、開く YouTube の URL を作る。使えない値なら null。
    /// </summary>
    internal static string? BgmUrl(string? value)
    {
        value = value?.Trim() ?? "";
        if (value.Length == 0) return null;

        var listMatch = System.Text.RegularExpressions.Regex.Match(value, @"[?&]list=([\w-]+)");
        var videoMatch = System.Text.RegularExpressions.Regex.Match(value, @"(?:[?&]v=|youtu\.be/)([\w-]+)");
        bool isUrl = value.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("youtu.be", StringComparison.OrdinalIgnoreCase);

        string list, video;
        if (isUrl)
        {
            if (!listMatch.Success && !videoMatch.Success) return null;
            list = listMatch.Success ? listMatch.Groups[1].Value : "";
            video = videoMatch.Success ? videoMatch.Groups[1].Value : "";
        }
        else
        {
            // URL ではなく再生リストの ID だけが書かれている
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^[\w-]{10,}$")) return null;
            list = value;
            video = "";
        }

        // 動画と再生リストを両方指定すると、その動画から再生リストが自動で流れる
        // （再生リストによっては list= だけだと「この端末では利用できません」と出るので、そのときは動画付きの URL を貼る）
        if (video.Length > 0 && list.Length > 0)
            return $"https://www.youtube.com/watch?v={Uri.EscapeDataString(video)}&list={Uri.EscapeDataString(list)}";
        if (video.Length > 0)
            return $"https://www.youtube.com/watch?v={Uri.EscapeDataString(video)}";
        return $"https://www.youtube.com/playlist?list={Uri.EscapeDataString(list)}";
    }

    /// <summary>設定した YouTube の再生リストを既定のブラウザで開く。設定がなければ false</summary>
    internal static bool OpenBgmPlaylist()
    {
        string? url = BgmUrl(IslandConfig.Load().BgmPlaylist);
        if (url == null) return false;
        // 裏で隠して流すと Chrome がふつうに使えなくなるので、既定のブラウザでふつうに開く
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
        return true;
    }
    private void OnMediaNext(object sender, RoutedEventArgs e) => _media.Next();
    private void OnMediaPrev(object sender, RoutedEventArgs e) => _media.Previous();
    private void OnToggleMute(object sender, RoutedEventArgs e) => _audio.ToggleMute();

    private static void BuildWave(Panel host, List<Rectangle> bars, int count, double width, double gap)
    {
        for (int i = 0; i < count; i++)
        {
            var r = new Rectangle
            {
                Width = width,
                Height = 3,
                RadiusX = width / 2,
                RadiusY = width / 2,
                Fill = Brushes.White,
                Margin = new Thickness(i == 0 ? 0 : gap, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            bars.Add(r);
            host.Children.Add(r);
        }
    }

    /// <summary>音に合わせて揺れているように見える波形（実際の音量ではなく、それらしい動き）</summary>
    private void AnimateWave(List<Rectangle> bars, bool playing, double max)
    {
        double min = 3;
        for (int i = 0; i < bars.Count; i++)
        {
            double target = playing ? min + (max - min) * (0.25 + 0.75 * _random.NextDouble()) : min;
            var bar = bars[i];
            bar.Height += (target - bar.Height) * (playing ? 0.55 : 0.3);
        }
    }

    private string VolumeGlyph()
    {
        if (_audio.Muted || _audio.Level < 0.005) return "";
        return _audio.Level < 0.34 ? "" : _audio.Level < 0.67 ? "" : "";
    }

    // ===================== タイマー =====================

    private TimeSpan TimerRemaining
    {
        get
        {
            if (_timerEndsAt is not DateTime end) return _timerLeft;
            var left = end - DateTime.Now;
            return left < TimeSpan.Zero ? TimeSpan.Zero : left;
        }
    }

    private void SetTimer(TimeSpan duration)
    {
        _timerDuration = duration;
        _timerLeft = duration;
        _timerEndsAt = null;
        _timerStarted = false;
        UpdateTimerButtons();
        UpdateCompact();
        UpdateLive();
    }

    private void OnTimerAdjust(object sender, RoutedEventArgs e)
    {
        var delta = TimeSpan.FromSeconds(double.Parse((string)((Button)sender).Tag));
        if (_timerEndsAt is DateTime end)
        {
            // 動いている途中でも足し引きできる（残りが 1 秒未満にはしない）
            var newEnd = end + delta;
            if (newEnd - DateTime.Now < TimeSpan.FromSeconds(1)) return;
            _timerEndsAt = newEnd;
            _timerDuration += delta;
        }
        else if (_timerStarted)
        {
            if (_timerLeft + delta < TimeSpan.FromSeconds(1)) return;
            _timerLeft += delta;
            _timerDuration += delta;
        }
        else
        {
            var d = _timerDuration + delta;
            if (d < TimeSpan.FromMinutes(1) || d > TimeSpan.FromHours(24)) return;
            _timerDuration = _timerLeft = d;
        }
        UpdateLive();
    }

    private void OnTimerStartPause(object sender, RoutedEventArgs e)
    {
        if (_timerEndsAt != null)
        {
            _timerLeft = TimerRemaining; // 一時停止
            _timerEndsAt = null;
        }
        else
        {
            _timerEndsAt = DateTime.Now + _timerLeft; // 開始 / 再開
            _timerStarted = true;
        }
        UpdateTimerButtons();
        UpdateCompact();
    }

    private void OnTimerReset(object sender, RoutedEventArgs e) => SetTimer(_timerDuration);

    private void UpdateTimerButtons()
    {
        TimerStartButton.Content = _timerEndsAt != null ? "一時停止" : _timerStarted ? "再開" : "開始";
    }

    private void TimerFinished()
    {
        _timerEndsAt = null;
        _timerStarted = false;
        _timerLeft = _timerDuration;
        UpdateTimerButtons();

        var stop = new Button
        {
            Content = "停止",
            Style = (Style)FindResource("PillButton"),
            Background = new SolidColorBrush(Orange),
            Foreground = Brushes.Black,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(16, 6, 16, 6),
            Margin = new Thickness(0, 0, 6, 0),
        };
        stop.Click += (_, _) => StopAlarm();
        var again = new Button { Content = "+1分", Style = (Style)FindResource("PillButton"), Padding = new Thickness(12, 6, 12, 6) };
        again.Click += (_, _) =>
        {
            StopAlarm();
            SetTimer(TimeSpan.FromMinutes(1));
            OnTimerStartPause(again, new RoutedEventArgs()); // すぐにもう1分スタート
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(stop);
        buttons.Children.Add(again);

        _expanded = false;
        ShowAlert("timer-done", CircleGlyph("", Orange), "タイマー", "時間になりました",
            right: buttons, width: 380, height: 66, sticky: true);
        _alarmStarted = DateTime.Now;
        SystemSounds.Exclamation.Play();
        _alarm.Start();
    }

    private void StopAlarm()
    {
        _alarm.Stop();
        if (_alert?.Kind == "timer-done") DismissAlert();
        UpdateCompact();
    }

    // ===================== ストップウォッチ =====================

    private void OnStopwatchStartStop(object sender, RoutedEventArgs e)
    {
        if (_stopwatch.IsRunning) _stopwatch.Stop();
        else _stopwatch.Start();
        UpdateStopwatchButtons();
        UpdateCompact();
    }

    private void OnStopwatchLapReset(object sender, RoutedEventArgs e)
    {
        if (_stopwatch.IsRunning)
        {
            _laps.Add(_stopwatch.Elapsed);
            var lap = _stopwatch.Elapsed - (_laps.Count > 1 ? _laps[^2] : TimeSpan.Zero);
            var row = new DockPanel { Margin = new Thickness(0, 0, 8, 4) };
            var time = new TextBlock { Text = FormatStopwatch(lap), Foreground = Brushes.White, FontSize = 13 };
            DockPanel.SetDock(time, Dock.Right);
            row.Children.Add(time);
            row.Children.Add(new TextBlock { Text = $"ラップ {_laps.Count}", Foreground = new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)), FontSize = 13 });
            LapList.Children.Insert(0, row);
        }
        else
        {
            _stopwatch.Reset();
            _laps.Clear();
            LapList.Children.Clear();
        }
        UpdateStopwatchButtons();
        UpdateCompact();
        UpdateLive();
    }

    private void UpdateStopwatchButtons()
    {
        bool running = _stopwatch.IsRunning;
        StopwatchStartButton.Content = running ? "停止" : _stopwatch.Elapsed > TimeSpan.Zero ? "再開" : "開始";
        StopwatchStartButton.Background = new SolidColorBrush(running ? Red : Green);
        StopwatchLapButton.Content = running ? "ラップ" : "リセット";
        StopwatchLapButton.IsEnabled = running || _stopwatch.Elapsed > TimeSpan.Zero;
    }

    // ===================== お知らせ =====================

    private void ShowAlert(string kind, FrameworkElement? left, string title, string? sub = null,
        Brush? titleBrush = null, FrameworkElement? center = null, FrameworkElement? right = null,
        double width = 320, double height = 44, double seconds = 2.2, bool sticky = false)
    {
        // 大きく開いて操作している最中は、急ぎでないお知らせは出さない
        if (_expanded && !sticky) return;
        if (_alert?.Sticky == true && !sticky) return;

        _alert = new Alert(kind, width, height, sticky);
        AlertLeft.Content = left;
        AlertTitle.Text = title;
        AlertTitle.Foreground = titleBrush ?? Brushes.White;
        AlertTitle.Visibility = center == null ? Visibility.Visible : Visibility.Collapsed;
        AlertSub.Text = sub ?? "";
        AlertSub.Visibility = center == null && !string.IsNullOrEmpty(sub) ? Visibility.Visible : Visibility.Collapsed;
        AlertCenter.Content = center;
        AlertCenter.Visibility = center != null ? Visibility.Visible : Visibility.Collapsed;
        AlertRight.Content = right;

        _alertTimer.Stop();
        if (!sticky)
        {
            _alertTimer.Interval = TimeSpan.FromSeconds(seconds);
            _alertTimer.Start();
        }
        ApplyState();
    }

    private void DismissAlert()
    {
        _alertTimer.Stop();
        _alert = null;
        ApplyState();
    }

    private void OnVolumeChanged(bool muteChanged)
    {
        if (muteChanged)
        {
            // iPhone の消音スイッチと同じ見た目
            ShowAlert("mute", CircleGlyph(_audio.Muted ? "" : "", _audio.Muted ? Red : Color.FromRgb(0x8E, 0x8E, 0x93)),
                _audio.Muted ? "消音 オン" : "消音 オフ",
                titleBrush: _audio.Muted ? new SolidColorBrush(Red) : null,
                width: 230, height: 40, seconds: 1.6);
            return;
        }
        _hudBar.Value = _audio.Level;
        ShowAlert("volume", Glyph(VolumeGlyph(), 16, Colors.White), "", center: _hudBar,
            right: new TextBlock { Text = $"{Math.Round(_audio.Level * 100)}", Foreground = Brushes.White, FontSize = 13, Width = 26, TextAlignment = TextAlignment.Right },
            width: 270, height: 40, seconds: 1.4);
    }

    private void OnOutputDeviceChanged()
    {
        string name = _audio.DeviceName;
        bool headphone = new[] { "ヘッドホン", "ヘッドセット", "イヤホン", "Headphone", "Headset", "AirPods", "Buds", "Earbuds", "Hands-Free" }
            .Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        ShowAlert("device", CircleGlyph(headphone ? "" : "", Color.FromRgb(0x0A, 0x84, 0xFF)),
            string.IsNullOrEmpty(name) ? "オーディオ出力" : name, "音の出力先が切り替わりました",
            width: 360, height: 56, seconds: 2.6);
    }

    private void OnPowerChanged(PowerMonitor.Change change)
    {
        int pct = _power.Percent;
        switch (change)
        {
            case PowerMonitor.Change.PluggedIn:
                ShowAlert("power", null, "充電中", titleBrush: new SolidColorBrush(Green),
                    right: BatteryWithText(pct, Green), width: 300, height: 40, seconds: 2.6);
                break;
            case PowerMonitor.Change.Unplugged:
                ShowAlert("power", null, "電源から外れました",
                    right: BatteryWithText(pct, pct <= 20 ? Red : Colors.White), width: 320, height: 40, seconds: 2);
                break;
            case PowerMonitor.Change.Low:
                ShowAlert("power", BatteryIcon(pct, Red), "バッテリー残量が少なくなっています", $"残り {pct}%",
                    width: 380, height: 58, seconds: 4.5);
                break;
            case PowerMonitor.Change.SaverOn:
            case PowerMonitor.Change.SaverOff:
                bool on = change == PowerMonitor.Change.SaverOn;
                ShowAlert("power", BatteryIcon(pct, on ? Yellow : Colors.White), on ? "省電力モード オン" : "省電力モード オフ",
                    titleBrush: on ? new SolidColorBrush(Yellow) : null, width: 290, height: 40, seconds: 2);
                break;
        }
    }

    private void OnPrivacyStarted(bool camera, string app)
    {
        ShowAlert("privacy", CircleGlyph(camera ? "" : "", camera ? Green : Orange),
            camera ? "カメラを使用中" : "マイクを使用中", app, width: 320, height: 56, seconds: 2.4);
        UpdateSideItems();
    }

    // ===================== 見た目の部品 =====================

    private static TextBlock Glyph(string glyph, double size, Color color) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily(IconFont),
        FontSize = size,
        Foreground = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>色付きの丸の中にアイコン</summary>
    private static Border CircleGlyph(string glyph, Color color) => new()
    {
        Width = 28,
        Height = 28,
        CornerRadius = new CornerRadius(14),
        Background = new SolidColorBrush(Color.FromArgb(0x38, color.R, color.G, color.B)),
        Child = Glyph(glyph, 14, color),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>iPhone 風の電池アイコン</summary>
    private static FrameworkElement BatteryIcon(int percent, Color color)
    {
        var outline = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF));
        var body = new Border
        {
            Width = 28,
            Height = 14,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1.3),
            BorderBrush = outline,
            Padding = new Thickness(1.6),
            Child = new Border
            {
                Width = Math.Max(2, 22 * Math.Clamp(percent, 0, 100) / 100.0),
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(color),
            },
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(body);
        panel.Children.Add(new Border
        {
            Width = 2,
            Height = 5,
            CornerRadius = new CornerRadius(0, 1.5, 1.5, 0),
            Background = outline,
            Margin = new Thickness(1, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private static FrameworkElement BatteryWithText(int percent, Color color)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = $"{percent}%",
            Foreground = new SolidColorBrush(color),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(BatteryIcon(percent, color));
        return panel;
    }

    // ===================== 時間の表示 =====================

    private static string FormatClock(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>タイマーは切り上げて表示（残り 0.4 秒なら「0:01」）</summary>
    private static string FormatTimer(TimeSpan t) => FormatClock(TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, t.TotalSeconds))));

    private static string FormatStopwatch(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}"
                          : $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
}
