# Generates assets\OptiWin.ico (run once, before build.ps1). Uses only WPF, which ships with Windows.
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$out = Join-Path $PSScriptRoot '..\assets'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$bolt = [Windows.Media.Geometry]::Parse('M13 2L3 14h9l-1 8 10-12h-9l1-8z')
$bg = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString('#3DDBB4'))
$fg = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString('#04241B'))
$pngs = @()
foreach ($s in 256, 64, 48, 32, 24, 16) {
    $v = New-Object Windows.Media.DrawingVisual
    $dc = $v.RenderOpen()
    $r = $s * 0.22
    $dc.DrawRoundedRectangle($bg, $null, (New-Object Windows.Rect 0, 0, $s, $s), $r, $r)
    $scale = $s / 24.0 * 0.74
    $off = ($s - 24 * $scale) / 2
    $dc.PushTransform((New-Object Windows.Media.MatrixTransform $scale, 0, 0, $scale, $off, $off))
    $dc.DrawGeometry($fg, $null, $bolt)
    $dc.Pop()
    $dc.Close()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $s, $s, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($v)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    $pngs += , @($s, $ms.ToArray())
}
$fs = [IO.File]::Create((Join-Path $out 'OptiWin.ico'))
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $sz = if ($p[0] -ge 256) { 0 } else { $p[0] }
    $w.Write([byte]$sz); $w.Write([byte]$sz); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$p[1].Length); $w.Write([uint32]$offset)
    $offset += $p[1].Length
}
foreach ($p in $pngs) { $w.Write([byte[]]$p[1]) }
$w.Close()
Write-Host "Icon written: $out\OptiWin.ico"
