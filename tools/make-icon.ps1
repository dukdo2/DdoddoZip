# Builds the DdoddoZip app icon (house made of blocks) as a multi-size .ico.
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
#       Rebuilds Assets\app.ico from Assets\app-source.png (transparent, 1024 px).
#   powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 -Source picture.webp
#       First cuts the plain background out of a picture and saves it as app-source.png.
#       Only the background connected to the picture's border is removed, so areas of the same
#       colour inside the drawing stay. (WebP needs the Windows "WebP Image Extensions".)
param(
    [string]$Source = "",
    [string]$Png = "$PSScriptRoot\..\src\DdoddoZip.App\Assets\app-source.png",
    [string]$Out = "$PSScriptRoot\..\src\DdoddoZip.App\Assets\app.ico",
    [int]$Threshold = 30,       # colour distance from the background that still counts as background
    [double]$Margin = 0.02      # empty border around the drawing, as a share of its size
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase, System.Xaml
Add-Type -ReferencedAssemblies PresentationCore, WindowsBase, System.Xaml -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static class IconTool
{
    static byte[] Load(string path, out int w, out int h)
    {
        var dec = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var conv = new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
        w = conv.PixelWidth; h = conv.PixelHeight;
        var px = new byte[w * h * 4];
        conv.CopyPixels(px, w * 4, 0);
        return px;
    }

    static void SavePng(byte[] px, int size, string path)
    {
        var bmp = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, px, size * 4);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using (var fs = File.Create(path)) enc.Save(fs);
    }

    static double Dist(byte[] px, int i, double[] bg)
    {
        double b = px[i] - bg[0], g = px[i + 1] - bg[1], r = px[i + 2] - bg[2];
        return Math.Sqrt(b * b + g * g + r * r);
    }

    // Cuts out the background that touches the border, softens the cut edge, crops to a square.
    public static string CutBackground(string src, string dstPng, int outSize, int threshold, double margin)
    {
        int w, h;
        var px = Load(src, out w, out h);

        var bg = new double[3];
        int[] corners = { 0, w - 1, (h - 1) * w, h * w - 1 };
        foreach (int c in corners) for (int k = 0; k < 3; k++) bg[k] += px[c * 4 + k] / 4.0;

        var isBg = new bool[w * h];
        var queue = new Queue<int>();
        Action<int> seed = p => { if (!isBg[p] && Dist(px, p * 4, bg) < threshold) { isBg[p] = true; queue.Enqueue(p); } };
        for (int x = 0; x < w; x++) { seed(x); seed((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { seed(y * w); seed(y * w + w - 1); }
        while (queue.Count > 0)
        {
            int p = queue.Dequeue(); int x = p % w, y = p / w;
            if (x > 0) seed(p - 1);
            if (x < w - 1) seed(p + 1);
            if (y > 0) seed(p - w);
            if (y < h - 1) seed(p + w);
        }

        // Edge pixels are a blend of drawing and background: turn the blend into transparency.
        var res = (byte[])px.Clone();
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int p = y * w + x;
            if (isBg[p]) { res[p * 4 + 3] = 0; continue; }
            if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;

            bool nearBg = false;
            for (int dy = -2; dy <= 2 && !nearBg; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= w || yy >= h || isBg[yy * w + xx]) { nearBg = true; break; }
            }
            if (!nearBg) continue;

            double d = Dist(px, p * 4, bg), full = d;
            for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || yy < 0 || xx >= w || yy >= h || isBg[yy * w + xx]) continue;
                double dd = Dist(px, (yy * w + xx) * 4, bg);
                if (dd > full) full = dd;
            }
            double a = full <= 0 ? 1 : Math.Min(1, d / full);
            if (a >= 0.999) continue;
            for (int k = 0; k < 3; k++)
            {
                double v = bg[k] + (px[p * 4 + k] - bg[k]) / Math.Max(a, 0.05);
                res[p * 4 + k] = (byte)Math.Max(0, Math.Min(255, v));
            }
            res[p * 4 + 3] = (byte)(a * 255);
        }
        if (maxX < 0) throw new InvalidOperationException("The whole picture looks like background.");

        // Square crop centred on the drawing.
        int bw = maxX - minX + 1, bh = maxY - minY + 1;
        int side = (int)Math.Ceiling(Math.Max(bw, bh) * (1 + 2 * margin));
        int ox = (minX + maxX + 1) / 2 - side / 2, oy = (minY + maxY + 1) / 2 - side / 2;
        var sq = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
        {
            int sy = y + oy; if (sy < 0 || sy >= h) continue;
            for (int x = 0; x < side; x++)
            {
                int sx = x + ox; if (sx < 0 || sx >= w) continue;
                Buffer.BlockCopy(res, (sy * w + sx) * 4, sq, (y * side + x) * 4, 4);
            }
        }
        SavePng(Resize(sq, side, outSize), outSize, dstPng);
        return string.Format("background #{0:X2}{1:X2}{2:X2}, drawing {3}x{4} px of {5}x{6}",
            (int)bg[2], (int)bg[1], (int)bg[0], bw, bh, w, h);
    }

    // Area-average resize of a square BGRA picture (averaged with premultiplied alpha).
    public static byte[] Resize(byte[] src, int n, int s)
    {
        var dst = new byte[s * s * 4];
        double scale = (double)n / s;
        for (int ty = 0; ty < s; ty++)
        for (int tx = 0; tx < s; tx++)
        {
            double x0 = tx * scale, x1 = (tx + 1) * scale, y0 = ty * scale, y1 = (ty + 1) * scale;
            double sb = 0, sg = 0, sr = 0, sa = 0, sw = 0;
            for (int y = (int)y0; y < Math.Min(n, (int)Math.Ceiling(y1)); y++)
            {
                double wy = Math.Min(y + 1, y1) - Math.Max(y, y0);
                for (int x = (int)x0; x < Math.Min(n, (int)Math.Ceiling(x1)); x++)
                {
                    double wgt = wy * (Math.Min(x + 1, x1) - Math.Max(x, x0));
                    int i = (y * n + x) * 4; double a = src[i + 3] / 255.0;
                    sb += src[i] * a * wgt; sg += src[i + 1] * a * wgt; sr += src[i + 2] * a * wgt; sa += a * wgt; sw += wgt;
                }
            }
            int o = (ty * s + tx) * 4;
            if (sa > 0)
            {
                dst[o] = (byte)Math.Round(sb / sa); dst[o + 1] = (byte)Math.Round(sg / sa); dst[o + 2] = (byte)Math.Round(sr / sa);
                dst[o + 3] = (byte)Math.Round(255 * sa / sw);
            }
        }
        return dst;
    }

    // ICO: classic 32-bit DIB entries for small sizes (widest compatibility), PNG for 256.
    public static void WriteIco(string png, string ico, int[] sizes)
    {
        int w, h;
        var src = Load(png, out w, out h);
        if (w != h) throw new InvalidOperationException("The source PNG must be square.");
        var images = new List<byte[]>();
        foreach (int s in sizes)
        {
            var px = s == w ? src : Resize(src, w, s);
            var ms = new MemoryStream();
            if (s >= 256)
            {
                var bmp = BitmapSource.Create(s, s, 96, 96, PixelFormats.Bgra32, null, px, s * 4);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(bmp)); enc.Save(ms);
            }
            else
            {
                var bw = new BinaryWriter(ms);
                int maskStride = (int)Math.Ceiling(s / 32.0) * 4;
                bw.Write((uint)40); bw.Write(s); bw.Write(s * 2); bw.Write((ushort)1); bw.Write((ushort)32); bw.Write((uint)0);
                bw.Write((uint)(s * s * 4 + maskStride * s)); bw.Write(0); bw.Write(0); bw.Write((uint)0); bw.Write((uint)0);
                for (int y = s - 1; y >= 0; y--) bw.Write(px, y * s * 4, s * 4);
                bw.Write(new byte[maskStride * s]);
                bw.Flush();
            }
            images.Add(ms.ToArray());
        }
        using (var fs = File.Create(ico))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                bw.Write(dim); bw.Write(dim); bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32); bw.Write((uint)images[i].Length); bw.Write((uint)offset);
                offset += images[i].Length;
            }
            foreach (var img in images) bw.Write(img);
        }
    }
}
"@

if ($Source) {
    "Cut background: " + [IconTool]::CutBackground((Resolve-Path $Source).Path, $Png, 1024, $Threshold, $Margin)
    "Wrote $Png"
}
[IconTool]::WriteIco((Resolve-Path $Png).Path, $Out, @(16, 20, 24, 32, 40, 48, 64, 256))
"Wrote $Out"
