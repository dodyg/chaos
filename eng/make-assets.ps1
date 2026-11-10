# Regenerates Chaos's icon files (PLAN §13 packaging): the tray/window icon and the MSIX logos.
# The mark is a white eight-pointed star on the Chaos orange rounded square.
# Usage:  pwsh -File eng/make-assets.ps1   (Windows; uses System.Drawing)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$iconDir = Join-Path $root "src/Chaos.Desktop/Icons"
$logoDir = Join-Path $root "packaging/Images"
New-Item -ItemType Directory -Force $iconDir, $logoDir | Out-Null

function New-Mark([int]$width, [int]$height, [double]$markScale) {
    $bitmap = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $side = [Math]::Min($width, $height)
    $x = ($width - $side) / 2.0; $y = ($height - $side) / 2.0
    $radius = $side * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $radius * 2, $radius * 2, 180, 90)
    $path.AddArc($x + $side - $radius * 2, $y, $radius * 2, $radius * 2, 270, 90)
    $path.AddArc($x + $side - $radius * 2, $y + $side - $radius * 2, $radius * 2, $radius * 2, 0, 90)
    $path.AddArc($x, $y + $side - $radius * 2, $radius * 2, $radius * 2, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 234, 88, 12))), $path)

    $cx = $width / 2.0; $cy = $height / 2.0
    $outer = $side * $markScale / 2.0; $inner = $outer * 0.42
    $points = for ($i = 0; $i -lt 16; $i++) {
        $angle = [Math]::PI * $i / 8.0 - [Math]::PI / 2.0
        $r = if ($i % 2 -eq 0) { $outer } else { $inner }
        New-Object System.Drawing.PointF ([float]($cx + $r * [Math]::Cos($angle))), ([float]($cy + $r * [Math]::Sin($angle)))
    }
    $g.FillPolygon([System.Drawing.Brushes]::White, [System.Drawing.PointF[]]$points)
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
foreach ($size in $sizes) { $frames.Add([byte[]](Get-PngBytes (New-Mark $size $size 0.72))) }
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
    "Square44x44Logo.png"   = @(44, 44, 0.72)
    "Square150x150Logo.png" = @(150, 150, 0.6)
    "Wide310x150Logo.png"   = @(310, 150, 0.6)
    "StoreLogo.png"         = @(50, 50, 0.72)
}
foreach ($name in $logos.Keys) {
    $spec = $logos[$name]
    [System.IO.File]::WriteAllBytes((Join-Path $logoDir $name), (Get-PngBytes (New-Mark $spec[0] $spec[1] $spec[2])))
}

Write-Host "Assets written to $iconDir and $logoDir"
