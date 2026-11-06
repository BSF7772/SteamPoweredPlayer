// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S1: tiny Win32 + screen-capture helpers used only by the automation (selftest / perf).
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace S1App;

internal static partial class W32
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ClientToScreen(nint hwnd, ref POINT p);
    [LibraryImport("user32.dll")] public static partial int GetSystemMetrics(int index);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static partial bool SetForegroundWindow(nint hwnd);

    public static (int X, int Y) ClientOrigin(nint hwnd) { var p = new POINT(); ClientToScreen(hwnd, ref p); return (p.X, p.Y); }
    public static (int W, int H) PrimaryScreen() => (GetSystemMetrics(0), GetSystemMetrics(1));
}

/// <summary>Pixel statistics of a screen rectangle (BGRA32). Also saves the PNG so a human can look at it.</summary>
internal sealed record Shot(int W, int H, double MeanLuma, double StdLuma, double NonBlack, byte[] Bgra)
{
    public static Shot Capture(Rectangle r, string? pngPath = null)
    {
        using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
        if (pngPath is not null) bmp.Save(pngPath, ImageFormat.Png);
        var d = bmp.LockBits(new Rectangle(0, 0, r.Width, r.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var buf = new byte[d.Stride * r.Height];
        Marshal.Copy(d.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(d);
        double sum = 0, sum2 = 0; long nb = 0, n = (long)r.Width * r.Height;
        for (int y = 0; y < r.Height; y++)
            for (int x = 0; x < r.Width; x++)
            {
                int i = y * d.Stride + x * 4;
                double l = 0.114 * buf[i] + 0.587 * buf[i + 1] + 0.299 * buf[i + 2];
                sum += l; sum2 += l * l; if (l > 16) nb++;
            }
        double mean = sum / n;
        return new(r.Width, r.Height, mean, Math.Sqrt(Math.Max(0, sum2 / n - mean * mean)), (double)nb / n, buf);
    }

    /// <summary>Fraction of pixels in <paramref name="inner"/> (relative to this shot) whose channel difference to <paramref name="o"/> exceeds 24.</summary>
    public double ChangedFraction(Shot o, Rectangle inner)
    {
        long changed = 0, n = 0; int stride = W * 4;
        for (int y = Math.Max(0, inner.Top); y < Math.Min(H, inner.Bottom); y++)
            for (int x = Math.Max(0, inner.Left); x < Math.Min(W, inner.Right); x++)
            {
                int i = y * stride + x * 4; n++;
                if (Math.Abs(Bgra[i] - o.Bgra[i]) + Math.Abs(Bgra[i + 1] - o.Bgra[i + 1]) + Math.Abs(Bgra[i + 2] - o.Bgra[i + 2]) > 24) changed++;
            }
        return n == 0 ? 0 : (double)changed / n;
    }

    /// <summary>Fraction of nearly-white pixels (>= 200 on all channels) inside <paramref name="inner"/>: evidence of the white overlay text.</summary>
    public double WhiteFraction(Rectangle inner)
    {
        long w = 0, n = 0; int stride = W * 4;
        for (int y = Math.Max(0, inner.Top); y < Math.Min(H, inner.Bottom); y++)
            for (int x = Math.Max(0, inner.Left); x < Math.Min(W, inner.Right); x++)
            {
                int i = y * stride + x * 4; n++;
                if (Bgra[i] >= 200 && Bgra[i + 1] >= 200 && Bgra[i + 2] >= 200) w++;
            }
        return n == 0 ? 0 : (double)w / n;
    }
}
