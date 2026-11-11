# Regenerates Chaos's icon files (PLAN §13 packaging): the tray/window icon and the MSIX logos.
# The mark is a looping scribble that resolves into a checkmark ("a feeble attempt to organize
# modern life"), drawn in white on a blue gradient tile with a few stray sparks of chaos.
# The same geometry lives in eng/assets/chaos-icon.svg; keep the two in step.
# Usage:  pwsh -File eng/make-assets.ps1 [-OutDir <folder>]   (Windows; uses System.Drawing)

param([string]$OutDir)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$iconDir = if ($OutDir) { $OutDir } else { Join-Path $root "src/Chaos.Desktop/Icons" }
$logoDir = if ($OutDir) { $OutDir } else { Join-Path $root "packaging/Images" }
New-Item -ItemType Directory -Force $iconDir, $logoDir | Out-Null

# Mark geometry in a 100x100 box: start point, then cubic Bezier segments (c1, c2, end).
$start = @(10, 64)
$segments = @(
    @(26, 60, 46, 52, 55, 36),   # sweep up and right
    @(62, 22, 54, 8, 40, 12),    # over the top of the loop
    @(26, 17, 26, 44, 40, 78),   # back down through the stroke to the tick's vertex
    @(52, 60, 68, 38, 90, 16)    # the long, decisive tick
)
$sparks = @(@(78, 78, 4.5), @(64, 90, 3), @(91, 62, 2.5))   # x, y, radius

function New-Mark([int]$width, [int]$height, [double]$markScale) {
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $side = [Math]::Min($width, $height)
    $x = ($width - $side) / 2.0; $y = ($height - $side) / 2.0
    $radius = $side * 0.22
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($x, $y, $radius * 2, $radius * 2, 180, 90)
    $tile.AddArc($x + $side - $radius * 2, $y, $radius * 2, $radius * 2, 270, 90)
    $tile.AddArc($x + $side - $radius * 2, $y + $side - $radius * 2, $radius * 2, $radius * 2, 0, 90)
    $tile.AddArc($x, $y + $side - $radius * 2, $radius * 2, $radius * 2, 90, 90)
    $tile.CloseFigure()
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF ([float]$x), ([float]$y)),
        (New-Object System.Drawing.PointF ([float]($x + $side)), ([float]($y + $side))),
        [System.Drawing.Color]::FromArgb(255, 14, 165, 233),
        [System.Drawing.Color]::FromArgb(255, 7, 89, 133))
    $g.FillPath($gradient, $tile)

    $unit = $side * $markScale / 100.0
    $ox = $width / 2.0 - 50 * $unit; $oy = $height / 2.0 - 46 * $unit   # mark sits slightly high in its box
    function P([double]$px, [double]$py) { New-Object System.Drawing.PointF ([float]($ox + $px * $unit)), ([float]($oy + $py * $unit)) }

    $mark = New-Object System.Drawing.Drawing2D.GraphicsPath
    $from = P $start[0] $start[1]
    foreach ($s in $segments) {
        $to = P $s[4] $s[5]
        $mark.AddBezier($from, (P $s[0] $s[1]), (P $s[2] $s[3]), $to)
        $from = $to
    }
    # Small sizes need a proportionally heavier stroke to stay legible.
    $strokeWidth = [Math]::Max(8.5 * $unit, $side * 0.11)
    if ($side -le 24) { $strokeWidth = $side * 0.12 }

    # Soft shadow under the mark for depth on larger sizes.
    if ($side -ge 48) {
        $shadow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(90, 8, 47, 73)), ([float]$strokeWidth)
        $shadow.StartCap = $shadow.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $shadow.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        $g.TranslateTransform(0, [float]($side * 0.025))
        $g.DrawPath($shadow, $mark)
        $g.ResetTransform()
    }

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([float]$strokeWidth)
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $mark)

    if ($side -ge 48) {
        $sparkBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 186, 230, 253))
        foreach ($s in $sparks) {
            $r = $s[2] * $unit
            $c = P $s[0] $s[1]
            $g.FillEllipse($sparkBrush, [float]($c.X - $r), [float]($c.Y - $r), [float](2 * $r), [float](2 * $r))
        }
    }
    $g.Dispose()
    return $bitmap
}

function Get-PngBytes($bitmap) {
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$stream.ToArray()
}

# ICO with PNG frames (supported since Windows Vista).
$sizes = 16, 20, 24, 32, 48, 64, 256
$frames = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($size in $sizes) {
    $scale = if ($size -le 24) { 0.84 } else { 0.74 }
    $frames.Add([byte[]](Get-PngBytes (New-Mark $size $size $scale)))
}
$ico = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $ico
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $iconDir "chaos.ico"), $ico.ToArray())

# MSIX visual assets (AppxManifest.xml names these files).
$logos = @{
    "Square44x44Logo.png"   = @(44, 44, 0.74)
    "Square150x150Logo.png" = @(150, 150, 0.66)
    "Wide310x150Logo.png"   = @(310, 150, 0.66)
    "StoreLogo.png"         = @(50, 50, 0.74)
}
foreach ($name in $logos.Keys) {
    $spec = $logos[$name]
    [System.IO.File]::WriteAllBytes((Join-Path $logoDir $name), (Get-PngBytes (New-Mark $spec[0] $spec[1] $spec[2])))
}

if ($OutDir) {
    foreach ($size in 16, 24, 32, 256) {
        $scale = if ($size -le 24) { 0.84 } else { 0.74 }
        [System.IO.File]::WriteAllBytes((Join-Path $OutDir "preview-$size.png"), (Get-PngBytes (New-Mark $size $size $scale)))
    }
}

Write-Host "Assets written to $iconDir and $logoDir"
