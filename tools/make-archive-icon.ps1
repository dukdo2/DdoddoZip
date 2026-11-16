# Generates the icon shown on archive files opened with DdoddoZip: a white page with a
# folded corner, a zipper down the middle and the mint app badge.
# Run with Windows PowerShell 5.1:  powershell -File tools\make-archive-icon.ps1
param([string]$Out = "$PSScriptRoot\..\src\DdoddoZip.App\Assets\archive.ico")

Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$accent = [System.Drawing.ColorTranslator]::FromHtml('#12A38A')
$edge = [System.Drawing.ColorTranslator]::FromHtml('#9AA3AD')
$fold = [System.Drawing.ColorTranslator]::FromHtml('#DDE2E7')
$images = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    # page with folded top-right corner
    $l = $s * 0.16; $t = $s * 0.06; $r = $s * 0.84; $b = $s * 0.94; $c = $s * 0.24
    $page = New-Object System.Drawing.Drawing2D.GraphicsPath
    $page.AddPolygon([System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $l, $t), (New-Object System.Drawing.PointF ($r - $c), $t),
        (New-Object System.Drawing.PointF $r, ($t + $c)), (New-Object System.Drawing.PointF $r, $b),
        (New-Object System.Drawing.PointF $l, $b)))
    $g.FillPath([System.Drawing.Brushes]::White, $page)
    $pen = New-Object System.Drawing.Pen $edge, ([Math]::Max(1, $s / 32))
    $g.DrawPath($pen, $page)
    $corner = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($r - $c), $t), (New-Object System.Drawing.PointF ($r - $c), ($t + $c)),
        (New-Object System.Drawing.PointF $r, ($t + $c)))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $fold), $corner)
    $g.DrawPolygon($pen, $corner)

    # zipper teeth down the left third
    if ($s -ge 24) {
        $tooth = New-Object System.Drawing.SolidBrush $edge
        $tw = $s * 0.07; $th = [Math]::Max(1, $s * 0.035); $zx = $s * 0.36
        $n = [int](($s * 0.42) / ($th * 2))
        for ($i = 0; $i -lt $n; $i++) {
            $x = $zx + ($(if ($i % 2) { $tw * 0.55 } else { -$tw * 0.55 }))
            $g.FillRectangle($tooth, $x, $t + $s * 0.05 + $i * $th * 2, $tw, $th)
        }
    }

    # mint badge with the app letter in the lower right
    $bs = $s * 0.5; $bx = $s * 0.42; $by = $s * 0.46; $br = $bs * 0.22
    $badge = New-Object System.Drawing.Drawing2D.GraphicsPath
    $badge.AddArc($bx, $by, 2 * $br, 2 * $br, 180, 90)
    $badge.AddArc($bx + $bs - 2 * $br, $by, 2 * $br, 2 * $br, 270, 90)
    $badge.AddArc($bx + $bs - 2 * $br, $by + $bs - 2 * $br, 2 * $br, 2 * $br, 0, 90)
    $badge.AddArc($bx, $by + $bs - 2 * $br, 2 * $br, 2 * $br, 90, 90)
    $badge.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), $badge)
    if ($s -ge 24) {
        $font = New-Object System.Drawing.Font 'Malgun Gothic', ($bs * 0.6), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
        $fmt = New-Object System.Drawing.StringFormat
        $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
        $rect = New-Object System.Drawing.RectangleF $bx, ($by + $bs * 0.05), $bs, $bs
        $g.DrawString([string][char]0xB610, $font, [System.Drawing.Brushes]::White, $rect, $fmt)
    }

    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Classic DIB entry (32-bit BGRA, bottom-up, plus 1-bit AND mask) for the widest compatibility.
        $w2 = New-Object System.IO.BinaryWriter $ms
        $w2.Write([UInt32]40); $w2.Write([Int32]$s); $w2.Write([Int32]($s * 2))
        $w2.Write([UInt16]1); $w2.Write([UInt16]32); $w2.Write([UInt32]0)
        $maskStride = [int]([Math]::Ceiling($s / 32.0) * 4)
        $w2.Write([UInt32]($s * $s * 4 + $maskStride * $s)); $w2.Write([Int32]0); $w2.Write([Int32]0); $w2.Write([UInt32]0); $w2.Write([UInt32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $px = $bmp.GetPixel($x, $y)
                $w2.Write([byte]$px.B); $w2.Write([byte]$px.G); $w2.Write([byte]$px.R); $w2.Write([byte]$px.A)
            }
        }
        $w2.Write((New-Object byte[] ($maskStride * $s)))
        $w2.Flush()
    }
    $images += , @($s, $ms.ToArray())
    $g.Dispose(); $bmp.Dispose()
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($p in $images) {
    $s = $p[0]; $data = $p[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $images) { $bw.Write([byte[]]$p[1]) }
$bw.Close()
"Wrote $Out"
