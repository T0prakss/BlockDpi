Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\src\BlockDpi\Assets'
New-Item -ItemType Directory -Force $out | Out-Null
function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 512.0
    $r = 112 * $s; $m = 16 * $s; $w = $size - 2 * $m
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $r, $r, 180, 90)
    $path.AddArc($m + $w - $r, $m, $r, $r, 270, 90)
    $path.AddArc($m + $w - $r, $m + $w - $r, $r, $r, 0, 90)
    $path.AddArc($m, $m + $w - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 167, 139, 250)), ([System.Drawing.Color]::FromArgb(255, 91, 33, 182)), 45.0
    $g.FillPath($brush, $path)
    $shield = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = @(
        (New-Object System.Drawing.PointF (256 * $s), (100 * $s)),
        (New-Object System.Drawing.PointF (392 * $s), (150 * $s)),
        (New-Object System.Drawing.PointF (392 * $s), (250 * $s))
    )
    $shield.AddLine($pts[0], $pts[1])
    $shield.AddLine($pts[1], $pts[2])
    $shield.AddBezier((392 * $s), (250 * $s), (392 * $s), (340 * $s), (330 * $s), (395 * $s), (256 * $s), (420 * $s))
    $shield.AddBezier((256 * $s), (420 * $s), (182 * $s), (395 * $s), (120 * $s), (340 * $s), (120 * $s), (250 * $s))
    $shield.AddLine((120 * $s), (250 * $s), (120 * $s), (150 * $s))
    $shield.CloseFigure()
    $g.FillPath([System.Drawing.Brushes]::White, $shield)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 124, 58, 237)), (30 * $s)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLines($pen, @(
        (New-Object System.Drawing.PointF (200 * $s), (265 * $s)),
        (New-Object System.Drawing.PointF (244 * $s), (310 * $s)),
        (New-Object System.Drawing.PointF (318 * $s), (215 * $s))
    ))
    $g.Dispose()
    return $bmp
}
$big = New-IconBitmap 512
$big.Save((Join-Path $out 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$sizes = 256, 64, 48, 32, 16
$pngs = foreach ($sz in $sizes) {
    $b = New-IconBitmap $sz
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create((Join-Path $out 'icon.ico'))
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Close()
$ms256 = New-Object System.IO.MemoryStream; (New-IconBitmap 256).Save($ms256, [System.Drawing.Imaging.ImageFormat]::Png)
$ms512 = New-Object System.IO.MemoryStream; $big.Save($ms512, [System.Drawing.Imaging.ImageFormat]::Png)
$entries = @(@('ic08', $ms256.ToArray()), @('ic09', $ms512.ToArray()))
function BE32([int]$v) { $b = [BitConverter]::GetBytes([uint32]$v); [Array]::Reverse($b); return , $b }
$total = 8; foreach ($e in $entries) { $total += 8 + $e[1].Length }
$icns = New-Object System.IO.MemoryStream
$icns.Write([Text.Encoding]::ASCII.GetBytes('icns'), 0, 4); $t = BE32 $total; $icns.Write($t, 0, 4)
foreach ($e in $entries) {
    $icns.Write([Text.Encoding]::ASCII.GetBytes($e[0]), 0, 4); $l = BE32 (8 + $e[1].Length); $icns.Write($l, 0, 4)
    $icns.Write($e[1], 0, $e[1].Length)
}
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'macos\icon.icns'), $icns.ToArray())
Write-Host "Ikonlar olusturuldu."
