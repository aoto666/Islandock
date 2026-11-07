using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Islandock;

/// <summary>
/// iPhone 風の細いバー（再生位置・音量・タイマーの残り）。
/// クリック・ドラッグで値を変えられる（IsHitTestVisible=false にすれば表示専用）。
/// </summary>
public sealed class IslandBar : Grid
{
    private readonly Border _track;
    private readonly Border _fill;
    private readonly double _thin, _thick;
    private double _value;

    public bool IsDragging { get; private set; }

    /// <summary>ドラッグ中、値が変わるたび</summary>
    public event Action<double>? UserChanged;
    /// <summary>ボタンを離したとき</summary>
    public event Action<double>? UserCommitted;

    public IslandBar(double thickness = 6, Color? fill = null)
    {
        _thin = thickness;
        _thick = thickness * 1.6; // 触っている間は少し太くなる（iPhone と同じ）
        Height = _thick + 8;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;

        _track = new Border { Background = new SolidColorBrush(Color.FromArgb(0x48, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center };
        _fill = new Border { Background = new SolidColorBrush(fill ?? Colors.White), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        Children.Add(_track);
        Children.Add(_fill);
        SetThickness(_thin);

        SizeChanged += (_, _) => Layout();
        MouseEnter += (_, _) => SetThickness(_thick);
        MouseLeave += (_, _) => { if (!IsDragging) SetThickness(_thin); };
        MouseLeftButtonDown += (_, e) =>
        {
            IsDragging = true;
            CaptureMouse();
            FromMouse(e);
            e.Handled = true;
        };
        MouseMove += (_, e) => { if (IsDragging) FromMouse(e); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!IsDragging) return;
            IsDragging = false;
            ReleaseMouseCapture();
            if (!IsMouseOver) SetThickness(_thin);
            UserCommitted?.Invoke(_value);
            e.Handled = true;
        };
        LostMouseCapture += (_, _) => IsDragging = false;
    }

    /// <summary>0〜1。ドラッグ中は外からの更新を無視する</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (IsDragging) return;
            _value = double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
            Layout();
        }
    }

    public Color FillColor
    {
        set => _fill.Background = new SolidColorBrush(value);
    }

    private void SetThickness(double t)
    {
        _track.Height = _fill.Height = t;
        _track.CornerRadius = _fill.CornerRadius = new CornerRadius(t / 2);
    }

    private void FromMouse(MouseEventArgs e)
    {
        _value = Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth), 0, 1);
        Layout();
        UserChanged?.Invoke(_value);
    }

    private void Layout() => _fill.Width = Math.Max(0, ActualWidth * _value);
}
