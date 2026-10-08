# Generates src/RemoteHub/Assets/app.ico (multi-size, PNG-compressed frames).
# Run with Windows PowerShell: powershell -ExecutionPolicy Bypass -File build\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "..\src\RemoteHub\Assets\app.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded square with accent gradient
    $r = [Math]::Max(2, $s * 0.22); $m = [Math]::Max(0.5, $s * 0.03)
    $rect = New-Object System.Drawing.RectangleF $m, $m, ($s - 2 * $m), ($s - 2 * $m)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($rect.X, $rect.Y, $r, $r, 180, 90)
    $path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 90)
    $path.AddArc($rect.Right - $r, $rect.Bottom - $r, $r, $r, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $c1 = [System.Drawing.Color]::FromArgb(255, 0, 95, 184); $c2 = [System.Drawing.Color]::FromArgb(255, 56, 182, 255)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $c1, $c2, 45
    $g.FillPath($brush, $path)

    # Hub: a centre node linked to four remote nodes (RemoteHub)
    $w = [single]$s
    $white = [System.Drawing.Color]::White
    $line = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(235, 255, 255, 255)), ([Math]::Max(1.1, $w * 0.055))
    $line.StartCap = 'Round'; $line.EndCap = 'Round'
    $cx = $w * 0.5; $cy = $w * 0.5
    $spokes = if ($s -ge 24) { @(@(0.24, 0.27), @(0.76, 0.27), @(0.24, 0.73), @(0.76, 0.73)) } else { @(@(0.22, 0.25), @(0.78, 0.25), @(0.5, 0.80)) }
    foreach ($p in $spokes) { $g.DrawLine($line, $cx, $cy, $w * $p[0], $w * $p[1]) }
    $node = New-Object System.Drawing.SolidBrush $white
    $nr = [Math]::Max(1.6, $w * 0.085)
    foreach ($p in $spokes) { $g.FillEllipse($node, $w * $p[0] - $nr, $w * $p[1] - $nr, 2 * $nr, 2 * $nr) }
    # Centre: white ring with the accent colour inside
    $cr = [Math]::Max(2.4, $w * 0.15)
    $g.FillEllipse($node, $cx - $cr, $cy - $cr, 2 * $cr, 2 * $cr)
    $inner = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 120, 212))
    $ir = $cr * 0.52
    $g.FillEllipse($inner, $cx - $ir, $cy - $ir, 2 * $ir, 2 * $ir)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($s in $sizes) { , (New-Frame $s) }

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset += $len
}
foreach ($f in $frames) { $bw.Write([byte[]]$f) }
$bw.Close()
"Wrote $out"
