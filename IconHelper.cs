using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Islandock.NativeMethods;

namespace Islandock;

/// <summary>
/// ファイルやアプリの「高解像度アイコン」を取り出す。
/// （Dock でアイコンを拡大してもボケないよう、256px で取得する）
/// </summary>
internal static class IconHelper
{
    public static ImageSource? GetIcon(string path, int size = 256)
    {
        // Steam などの .url は、中に書かれたアイコンファイルから直接読むと高画質になる
        if (path.EndsWith(".url", StringComparison.OrdinalIgnoreCase)
            && ReadUrlIconFile(path) is { } iconFile
            && (LoadIcoFile(iconFile) ?? GetShellImage(iconFile, size)) is { } urlIcon)
            return urlIcon;

        return GetShellImage(path, size);
    }

    /// <summary>.ico ファイルを直接読み、入っている中で一番大きい画像を返す</summary>
    private static BitmapSource? LoadIcoFile(string file)
    {
        if (!file.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var stream = System.IO.File.OpenRead(file);
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            BitmapSource best = decoder.Frames
                .OrderByDescending(f => f.PixelWidth)
                .ThenByDescending(f => f.Format.BitsPerPixel)
                .First();
            var converted = new FormatConvertedBitmap(best, PixelFormats.Pbgra32, null, 0);
            converted.Freeze();
            return converted;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>.url ファイルの IconFile= の行を読む</summary>
    private static string? ReadUrlIconFile(string urlPath)
    {
        try
        {
            foreach (var line in System.IO.File.ReadLines(urlPath))
            {
                if (!line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) continue;
                string file = Environment.ExpandEnvironmentVariables(line["IconFile=".Length..].Trim().Trim('"'));
                return System.IO.File.Exists(file) ? file : null;
            }
        }
        catch
        {
        }
        return null;
    }

    private static ImageSource? GetShellImage(string path, int size)
    {
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
            int hr = factory.GetImage(new NativeSize(size, size), SIIGBF.IconOnly | SIIGBF.BiggerSizeOk, out hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;
            return ToBitmapSource(hBitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
        }
    }

    /// <summary>Windows のビットマップを、透明部分を保ったまま WPF の画像に変換する</summary>
    private static BitmapSource? ToBitmapSource(IntPtr hBitmap)
    {
        var dib = new DIBSECTION();
        if (GetObject(hBitmap, Marshal.SizeOf<DIBSECTION>(), ref dib) == 0) return null;
        if (dib.dsBm.bmBits == IntPtr.Zero || dib.dsBm.bmBitsPixel != 32) return null;

        int w = dib.dsBm.bmWidth;
        int h = dib.dsBm.bmHeight;
        int stride = dib.dsBm.bmWidthBytes;

        var pixels = new byte[stride * h];
        Marshal.Copy(dib.dsBm.bmBits, pixels, 0, pixels.Length);

        // biHeight が正なら画像が上下逆に格納されているので、行を反転する
        if (dib.dsBmih.biHeight > 0)
        {
            var flipped = new byte[pixels.Length];
            for (int y = 0; y < h; y++)
                Buffer.BlockCopy(pixels, y * stride, flipped, (h - 1 - y) * stride, stride);
            pixels = flipped;
        }

        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>ショートカット(.lnk)なら、リンク先の実体パスを返す</summary>
    public static string ResolveShortcut(string path)
    {
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return path;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(path);
            string target = shortcut.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? path : target;
        }
        catch
        {
            return path;
        }
    }
}
