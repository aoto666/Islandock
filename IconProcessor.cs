using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Islandock;

/// <summary>アイコン画像を調べた結果</summary>
public sealed class IconAnalysis
{
    /// <summary>周りの透明な余白を切り取った画像</summary>
    public BitmapSource? Image { get; init; }

    /// <summary>四角く塗りつぶされた絵柄か（タイル全体に広げて表示する）</summary>
    public bool FullBleed { get; init; }

    /// <summary>絵柄の代表色</summary>
    public Color Dominant { get; init; } = Colors.White;

    /// <summary>色味がある絵柄か（白黒っぽいアイコンは false）</summary>
    public bool Colorful { get; init; }
}

/// <summary>
/// Windows のアイコンを macOS 風のタイルに載せるための下ごしらえ。
/// ・余白を切り取って大きさを揃える
/// ・四角い絵柄かどうかを判定する
/// ・代表色を求めてタイルの色に使う
/// </summary>
public static class IconProcessor
{
    public static IconAnalysis Analyze(ImageSource? source)
    {
        if (source is not BitmapSource src) return new IconAnalysis();
        if (src.Format != PixelFormats.Pbgra32)
            src = new FormatConvertedBitmap(src, PixelFormats.Pbgra32, null, 0);

        int w = src.PixelWidth, h = src.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        src.CopyPixels(px, stride, 0);

        // 1. 絵柄がある範囲（不透明な部分）を探す
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (px[y * stride + x * 4 + 3] <= 24) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) return new IconAnalysis { Image = src };

        int bw = maxX - minX + 1, bh = maxY - minY + 1;

        // 2. 不透明な割合と、色の鮮やかさで重み付けした代表色を求める
        int opaque = 0;
        double r = 0, g = 0, b = 0, weightSum = 0, satSum = 0;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int i = y * stride + x * 4;
                byte a = px[i + 3];
                if (a <= 200) continue;
                opaque++;

                // Pbgra32 は色にアルファが掛かっているので元に戻す
                double pb = px[i] * 255.0 / a, pg = px[i + 1] * 255.0 / a, pr = px[i + 2] * 255.0 / a;
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double sat = max <= 0 ? 0 : (max - min) / max;
                double weight = 0.03 + sat * sat * (max / 255.0);
                r += pr * weight; g += pg * weight; b += pb * weight;
                weightSum += weight;
                satSum += sat;
            }
        }

        double coverage = opaque / (double)(bw * bh);
        double aspect = bw / (double)bh;
        bool fullBleed = coverage > 0.9 && aspect > 0.88 && aspect < 1.14 && bw >= w * 0.6;

        var dominant = weightSum > 0
            ? Color.FromRgb(Clamp(r / weightSum), Clamp(g / weightSum), Clamp(b / weightSum))
            : Colors.White;
        bool colorful = opaque > 0 && satSum / opaque > 0.18;

        var cropped = new CroppedBitmap(src, new Int32Rect(minX, minY, bw, bh));
        cropped.Freeze();

        return new IconAnalysis { Image = cropped, FullBleed = fullBleed, Dominant = dominant, Colorful = colorful };
    }

    private static byte Clamp(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v)));

    /// <summary>2色を t の割合で混ぜる（t=0 で a、t=1 で b）</summary>
    public static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        Clamp(a.R + (b.R - a.R) * t),
        Clamp(a.G + (b.G - a.G) * t),
        Clamp(a.B + (b.B - a.B) * t));
}
