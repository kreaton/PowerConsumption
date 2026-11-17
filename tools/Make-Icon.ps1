# Draws PowerConsumption.Wpf\app.ico: a white lightning bolt over three bars on a rounded square
# that fades from the app's household green to its EV blue. Run it again after changing the drawing:
#   pwsh .\tools\Make-Icon.ps1
#
# Every size is drawn on its own (not scaled down from 256 px), so the small ones stay sharp.
# The frames are stored as PNG inside the .ico, which Windows has read since Vista.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out   = Join-Path $PSScriptRoot '..\PowerConsumption.Wpf\app.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-Frame([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    # Draw in a 256 × 256 design space.
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Rounded square, green (top left) to blue (bottom right): the colours of the charts.
    $inset = if ($size -le 20) { 0 } else { 8 }
    $rect = New-Object System.Drawing.RectangleF $inset, $inset, (256 - 2 * $inset), (256 - 2 * $inset)
    $r = 52
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.Left, $rect.Top, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($rect.Right - 2 * $r, $rect.Top, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($rect.Right - 2 * $r, $rect.Bottom - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($rect.Left, $rect.Bottom - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect,
        ([System.Drawing.Color]::FromArgb(27, 175, 122)), ([System.Drawing.Color]::FromArgb(42, 120, 214)), 45.0
    $g.FillPath($fill, $path)

    # Three faint bars behind the bolt (left out where they would only blur).
    if ($size -ge 32) {
        $bar = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 255, 255, 255))
        foreach ($b in @(@(44, 150), @(170, 120), @(196, 176))) {
            $h = 214 - $b[1]
            $g.FillRectangle($bar, $b[0], $b[1], 30, $h)
        }
    }

    # The bolt.
    [System.Drawing.PointF[]]$bolt = @(
        [System.Drawing.PointF]::new(146, 30), [System.Drawing.PointF]::new(68, 142),
        [System.Drawing.PointF]::new(120, 142), [System.Drawing.PointF]::new(102, 228),
        [System.Drawing.PointF]::new(190, 106), [System.Drawing.PointF]::new(138, 106),
        [System.Drawing.PointF]::new(178, 30))
    $g.FillPolygon([System.Drawing.Brushes]::White, $bolt)

    $g.Dispose()
    $bmp
}

# ICONDIR header, one 16-byte entry per frame, then the PNG data.
$pngs = foreach ($s in $sizes) {
    $bmp = New-Frame $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$file = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $file
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }   # 0 means 256
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Resolve-Path (Split-Path $out)).Path + '\app.ico', $file.ToArray())
Write-Output "Wrote $out ($($file.Length) bytes, sizes $($sizes -join ', '))"
