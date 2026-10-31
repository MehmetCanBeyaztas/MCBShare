# src/MCBShare.App/Assets/app.ico dosyasını üretir. Tasarım Core/Web/icon.svg ile aynıdır.
Add-Type -AssemblyName System.Drawing

function Draw-Icon([int]$size) {
    $s = $size / 512.0
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'

    function Rounded([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = 2 * $r
        $p.AddArc($x, $y, $d, $d, 180, 90)
        $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
        $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
        $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
        $p.CloseFigure()
        return $p
    }

    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $size, $size),
        [System.Drawing.Color]::FromArgb(76, 194, 255), [System.Drawing.Color]::FromArgb(0, 103, 192))
    $g.FillPath($bg, (Rounded 0 0 $size $size (112 * $s)))

    $white = [System.Drawing.Brushes]::White
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, 28 * $s))
    $g.DrawPath($pen, (Rounded (104 * $s) (136 * $s) (116 * $s) (240 * $s) (26 * $s)))
    $g.FillPath($white, (Rounded (300 * $s) (136 * $s) (108 * $s) (170 * $s) (20 * $s)))
    $g.FillPath($white, (Rounded (326 * $s) (334 * $s) (56 * $s) (28 * $s) (14 * $s)))

    $arrow = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, 24 * $s))
    $arrow.StartCap = 'Round'; $arrow.EndCap = 'Round'
    $g.DrawLine($arrow, 232 * $s, 256 * $s, 276 * $s, 256 * $s)
    $g.DrawLine($arrow, 256 * $s, 228 * $s, 284 * $s, 256 * $s)
    $g.DrawLine($arrow, 284 * $s, 256 * $s, 256 * $s, 284 * $s)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$images = foreach ($n in $sizes) { , (Draw-Icon $n) }

$out = Join-Path $PSScriptRoot '..\src\MCBShare.App\Assets'
New-Item -ItemType Directory -Force $out | Out-Null
$fs = [IO.File]::Create((Join-Path (Resolve-Path $out) 'app.ico'))
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $len = $images[$i].Length
    $b = if ($n -ge 256) { 0 } else { $n }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Dispose()
