# Generates the DdoddoZip app icon (mint rounded square with "또") as a multi-size .ico.
# Run with Windows PowerShell 5.1:  powershell -File tools\make-icon.ps1
param([string]$Out = "$PSScriptRoot\..\src\DdoddoZip.App\Assets\app.ico")

Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$accent = [System.Drawing.ColorTranslator]::FromHtml('#12A38A')
$pngs = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(0.5, $s * 0.04)
    $r = $s * 0.22
    $w = $s - 2 * $pad
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($pad, $pad, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($pad + $w - 2 * $r, $pad, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($pad + $w - 2 * $r, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($pad, $pad + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), $path)

    # zipper teeth along the top edge for larger sizes
    if ($s -ge 32) {
        $tooth = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 255, 255, 255))
        $tw = $s * 0.07
        for ($i = 0; $i -lt 4; $i++) {
            $x = $s * 0.5 - $tw / 2 + ($(if ($i % 2) { $tw * 0.6 } else { -$tw * 0.6 }))
            $g.FillRectangle($tooth, $x, $s * (0.08 + $i * 0.055), $tw, $s * 0.035)
        }
    }

    $fontSize = $s * 0.56
    $font = New-Object System.Drawing.Font 'Malgun Gothic', $fontSize, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $rect = New-Object System.Drawing.RectangleF 0, ($s * 0.06), $s, $s
    $g.DrawString([string][char]0xB610, $font, [System.Drawing.Brushes]::White, $rect, $fmt)

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
                $c = $bmp.GetPixel($x, $y)
                $w2.Write([byte]$c.B); $w2.Write([byte]$c.G); $w2.Write([byte]$c.R); $w2.Write([byte]$c.A)
            }
        }
        $w2.Write((New-Object byte[] ($maskStride * $s)))
        $w2.Flush()
    }
    $pngs += , @($s, $ms.ToArray())
    $g.Dispose(); $bmp.Dispose()
}

# ICO container with PNG-compressed images (supported since Windows Vista).
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $s = $p[0]; $data = $p[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$data.Length); $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p[1]) }
$bw.Close()
"Wrote $Out"
