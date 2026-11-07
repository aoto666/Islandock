using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Islandock;

/// <summary>
/// Dock 上のアイコン1つ分。
/// macOS（Big Sur 以降）のアイコンの作りを真似て、次の層を重ねている：
///   影 → スクワークルのタイル（背景色 → 絵柄 → 光沢 → 押下時の暗さ）→ 縁の光
/// 中身は 100x100 の設計図で作り、Viewbox で好きなサイズに拡大縮小している。
/// </summary>
public class DockItem
{
    private const double Design = 100;
    private const double TileSize = 80.5;                    // macOS の規格: 1024 中 824 がタイル
    private const double TileInset = (Design - TileSize) / 2;
    private const double ArtScale = 0.66;                    // タイルに載せる絵柄の大きさ

    private static readonly Geometry TileClip = Squircle.Scaled(TileSize);

    public DockEntry Entry { get; }

    /// <summary>実際の exe のパス（ショートカットの場合はリンク先）</summary>
    public string TargetPath => !IsTrash && !IsAddButton && !string.IsNullOrEmpty(Entry.ProcessName)
        ? Entry.ProcessName + ".exe"
        : _resolvedPath;

    private readonly string _resolvedPath;

    /// <summary>右端のごみ箱かどうか</summary>
    public bool IsTrash { get; }

    /// <summary>「＋」（アプリを追加）ボタンかどうか</summary>
    public bool IsAddButton { get; }

    /// <summary>画面に置く見た目の要素</summary>
    public Border Visual { get; }

    private readonly IconAnalysis _art;
    private readonly Grid _canvas;
    private readonly Viewbox _box;
    private readonly Path _shadow;
    private readonly Grid _tile;
    private readonly Rectangle _tileBg;
    private readonly Image _tileArt;
    private readonly Rectangle _pressOverlay;
    private readonly Path _rim;
    private readonly Image _plainArt;
    private readonly Ellipse _dot;
    private readonly TranslateTransform _bounce = new();
    private readonly Path? _plus;
    private bool _useTile;

    public DockItem(DockEntry entry, double baseSize, double gap, bool isTrash = false, bool isAddButton = false)
    {
        Entry = entry;
        IsTrash = isTrash;
        IsAddButton = isAddButton;
        _resolvedPath = isTrash || isAddButton ? entry.Path
            : entry.IsShellApp ? ""
            : IconHelper.ResolveShortcut(entry.Path);
        _art = isAddButton ? new IconAnalysis() : IconProcessor.Analyze(IconHelper.GetIcon(entry.Path));

        // --- 影（タイルの真下に柔らかく落ちる） ---
        _shadow = new Path
        {
            Data = Squircle.Unit,
            Stretch = Stretch.Fill,
            Width = TileSize,
            Height = TileSize,
            Fill = Brushes.Black,
            Opacity = 0.22,
            RenderTransform = new TranslateTransform(0, 1.5),
            Effect = new BlurEffect { Radius = 3.5, KernelType = KernelType.Gaussian },
        };

        // --- タイル本体（スクワークルで切り抜く） ---
        _tileBg = new Rectangle();

        _tileArt = new Image
        {
            Source = _art.Image,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        RenderOptions.SetBitmapScalingMode(_tileArt, BitmapScalingMode.HighQuality);

        // 上から光が当たっているような光沢
        var gloss = new Rectangle
        {
            Fill = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0.0),
                    new(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.45),
                    new(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.5),
                    new(Color.FromArgb(0x14, 0x00, 0x00, 0x00), 1.0),
                }, 90),
            IsHitTestVisible = false,
        };

        // 押している間だけ表示する、暗くするための重ね板
        _pressOverlay = new Rectangle
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
            Visibility = Visibility.Collapsed,
        };

        _tile = new Grid { Width = TileSize, Height = TileSize, Clip = TileClip };
        _tile.Children.Add(_tileBg);
        _tile.Children.Add(_tileArt);
        if (isAddButton)
        {
            // 「＋」ボタンは、空き枠のような半透明タイルに大きな＋を描く
            _plus = new Path
            {
                Data = Geometry.Parse("M 0,-22 L 0,22 M -22,0 L 22,0"),
                StrokeThickness = 7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _tile.Children.Add(_plus);
        }
        _tile.Children.Add(gloss);
        _tile.Children.Add(_pressOverlay);

        // --- 縁の光（上が明るく、下に向かって消える細い線） ---
        _rim = new Path
        {
            Data = Squircle.Unit,
            Stretch = Stretch.Fill,
            Width = TileSize,
            Height = TileSize,
            StrokeThickness = 0.9,
            Stroke = new LinearGradientBrush(
                Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 90),
            IsHitTestVisible = false,
        };

        // --- タイルを使わないとき用の絵柄 ---
        _plainArt = new Image
        {
            Source = _art.Image,
            Margin = new Thickness(TileInset),
            Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1.5, Direction = 270, Opacity = 0.3 },
        };
        RenderOptions.SetBitmapScalingMode(_plainArt, BitmapScalingMode.HighQuality);

        _canvas = new Grid
        {
            Width = Design,
            Height = Design,
            // 一度描いた絵を使い回して、拡大アニメーションを軽くする（2倍の解像度で保持）
            CacheMode = new BitmapCache(2.0),
        };
        foreach (var layer in new UIElement[] { _shadow, _tile, _rim, _plainArt })
        {
            if (layer is FrameworkElement fe)
            {
                fe.HorizontalAlignment = HorizontalAlignment.Center;
                fe.VerticalAlignment = VerticalAlignment.Center;
            }
            _canvas.Children.Add(layer);
        }

        _box = new Viewbox { Child = _canvas, Width = baseSize, Height = baseSize };

        _dot = new Ellipse
        {
            Width = 4,
            Height = 4,
            Margin = new Thickness(0, 2, 0, 0),
            Visibility = Visibility.Hidden,
        };

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        stack.Children.Add(_box);
        stack.Children.Add(_dot);

        Visual = new Border
        {
            Child = stack,
            Background = Brushes.Transparent, // 透明でもクリックを受け取れるようにする
            Margin = new Thickness(gap / 2, 0, gap / 2, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransformOrigin = new Point(0.5, 1),
            RenderTransform = new TransformGroup { Children = { _lift, _bounce } },
        };

        Visual.PreviewMouseLeftButtonDown += (_, _) => SetPressed(true);
        Visual.PreviewMouseLeftButtonUp += (_, _) => SetPressed(false);
        Visual.MouseLeave += (_, _) => SetPressed(false);
    }

    /// <summary>色とタイル表示の有無を反映する</summary>
    public void ApplyStyle(DockTheme theme, bool useTile)
    {
        _useTile = (useTile && !IsTrash) || IsAddButton;
        _dot.Fill = theme.Dot;

        if (IsAddButton)
        {
            var visibility = Visibility.Visible;
            _shadow.Visibility = Visibility.Collapsed;
            _tile.Visibility = visibility;
            _rim.Visibility = visibility;
            _plainArt.Visibility = Visibility.Collapsed;
            _tileBg.Fill = new SolidColorBrush(theme.IsDark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
            _plus!.Stroke = new SolidColorBrush(theme.IsDark ? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x3A, 0x3A, 0x3C));
            return;
        }

        var tileVisibility = _useTile ? Visibility.Visible : Visibility.Collapsed;
        _shadow.Visibility = tileVisibility;
        _tile.Visibility = tileVisibility;
        _rim.Visibility = tileVisibility;
        _plainArt.Visibility = _useTile ? Visibility.Collapsed : Visibility.Visible;

        if (!_useTile) return;

        if (_art.FullBleed)
        {
            // 四角い絵柄（ゲームの画像など）はタイル全体に広げ、角をスクワークルで切り抜く
            _tileBg.Fill = new SolidColorBrush(_art.Dominant);
            _tileArt.Stretch = Stretch.UniformToFill;
            _tileArt.Width = TileSize;
            _tileArt.Height = TileSize;
            _tileArt.RenderTransform = new ScaleTransform(1.06, 1.06); // 元の角丸を隠す
            _tileArt.Effect = null;
        }
        else
        {
            // それ以外は、絵柄の色から作った淡いグラデーションのタイルに載せる
            _tileBg.Fill = _art.Colorful
                ? new LinearGradientBrush(
                    IconProcessor.Mix(_art.Dominant, Colors.White, 0.86),
                    IconProcessor.Mix(_art.Dominant, Colors.White, 0.68), 90)
                : theme.TileFill;
            _tileArt.Stretch = Stretch.Uniform;
            _tileArt.Width = TileSize * ArtScale;
            _tileArt.Height = TileSize * ArtScale;
            _tileArt.RenderTransform = Transform.Identity;
            _tileArt.Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Direction = 270, Opacity = 0.22 };
        }
    }

    private void SetPressed(bool pressed)
    {
        if (_useTile)
            _pressOverlay.Visibility = pressed ? Visibility.Visible : Visibility.Collapsed;
        else
            _plainArt.Opacity = pressed ? 0.6 : 1.0;
    }

    /// <summary>アイコンの表示サイズ（拡大アニメーション用）</summary>
    public double Size
    {
        get => _box.Width;
        set { _box.Width = value; _box.Height = value; }
    }

    public bool IsRunning
    {
        set => _dot.Visibility = value ? Visibility.Visible : Visibility.Hidden;
    }

    // ===== 長押しでつまんで並べ替え =====

    private readonly ScaleTransform _lift = new(1, 1);

    /// <summary>本来の位置からのずれ（つまんで動かしているとき・並びが変わって滑るとき）</summary>
    public TranslateTransform Offset => _bounce;

    public bool IsBouncing => _isBouncing;

    /// <summary>つまみ上げた見た目（少し大きく・少し透ける）にする / 戻す</summary>
    public void SetLifted(bool lifted)
    {
        var duration = TimeSpan.FromSeconds(0.18);
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
        double scale = lifted ? 1.12 : 1.0;
        _lift.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
        _lift.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale, duration) { EasingFunction = ease });
        Visual.Opacity = lifted ? 0.88 : 1.0;
        Panel.SetZIndex(Visual, lifted ? 10 : 0);
        SetPressed(false);
    }

    /// <summary>ずれを、なめらかに 0 へ戻す</summary>
    public void SlideHome(double seconds = 0.22)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromSeconds(seconds);
        _bounce.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(_bounce.X, 0, duration) { EasingFunction = ease });
        _bounce.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(_bounce.Y, 0, duration) { EasingFunction = ease });
    }

    /// <summary>ずれを直接指定する（アニメーションを止めてから）</summary>
    public void SetOffset(double x, double y)
    {
        _bounce.BeginAnimation(TranslateTransform.XProperty, null);
        _bounce.BeginAnimation(TranslateTransform.YProperty, null);
        _bounce.X = x;
        _bounce.Y = y;
    }

    private bool _isBouncing;

    /// <summary>
    /// macOS と同じ起動アニメーション。
    /// アプリのウィンドウが出るまでアイコンが跳ね続け、出たら「その回の着地」で止まる。
    /// </summary>
    /// <param name="isReady">アプリの準備ができたかを返す関数（跳ねるたびに確認する）</param>
    public void BounceUntil(Func<bool> isReady, TimeSpan timeout)
    {
        if (_isBouncing) return;
        _isBouncing = true;
        var started = DateTime.Now;

        void BounceOnce()
        {
            double h = Size * 0.55; // 跳ねる高さはアイコンの半分ちょっと
            var anim = new DoubleAnimationUsingKeyFrames();
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            // 上がるときは減速、落ちるときは加速（重力っぽい放物線）
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(-h, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.32)),
                new SineEase { EasingMode = EasingMode.EaseOut }));
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.64)),
                new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            anim.Completed += (_, _) =>
            {
                // 最低1回は跳ねる。準備ができた / 時間切れなら止める
                if (isReady() || DateTime.Now - started > timeout)
                {
                    _isBouncing = false;
                    _bounce.BeginAnimation(TranslateTransform.YProperty, null);
                    _bounce.Y = 0;
                }
                else
                {
                    BounceOnce();
                }
            };
            _bounce.BeginAnimation(TranslateTransform.YProperty, anim);
        }

        BounceOnce();
    }
}
