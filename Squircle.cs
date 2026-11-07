using System.Windows;
using System.Windows.Media;

namespace Islandock;

/// <summary>
/// macOS のアイコンで使われる「スクワークル」（角が非常になめらかな角丸四角）の形を作る。
/// 数式 |x|^n + |y|^n = 1（超楕円）で描く。0〜1 の範囲の図形なので Stretch=Fill で拡大して使う。
/// </summary>
public static class Squircle
{
    public static readonly Geometry Unit = Create(5.0);

    /// <summary>size x size の大きさのスクワークル（切り抜き用）</summary>
    public static Geometry Scaled(double size)
    {
        var geometry = Unit.Clone();
        geometry.Transform = new ScaleTransform(size, size);
        geometry.Freeze();
        return geometry;
    }

    private static Geometry Create(double n)
    {
        const int steps = 160;
        var figure = new PathFigure { IsClosed = true, IsFilled = true };
        for (int i = 0; i < steps; i++)
        {
            double t = 2 * Math.PI * i / steps;
            double c = Math.Cos(t), s = Math.Sin(t);
            double x = Math.Sign(c) * Math.Pow(Math.Abs(c), 2 / n);
            double y = Math.Sign(s) * Math.Pow(Math.Abs(s), 2 / n);
            var p = new Point(0.5 + 0.5 * x, 0.5 + 0.5 * y);
            if (i == 0) figure.StartPoint = p;
            else figure.Segments.Add(new LineSegment(p, true));
        }
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }
}
