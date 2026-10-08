param([Parameter(Mandatory=$true)][string]$SourceImage)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File $PSCommandPath -SourceImage $SourceImage
    if ($LASTEXITCODE -ne 0) { throw 'Icon export failed.' }
    return
}
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = [Windows.Media.Imaging.BitmapFrame]::Create([Uri]::new([IO.Path]::GetFullPath($SourceImage)))
$rgba = [Windows.Media.Imaging.FormatConvertedBitmap]::new($source, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
$stride = $rgba.PixelWidth * 4
$pixels = [byte[]]::new($stride * $rgba.PixelHeight)
$rgba.CopyPixels($pixels, $stride, 0)
$left = $rgba.PixelWidth; $top = $rgba.PixelHeight; $right = 0; $bottom = 0
# Bound visible artwork, excluding almost-transparent pixels from generated PNG padding.
for ($y = 0; $y -lt $rgba.PixelHeight; $y++) {
    for ($x = 0; $x -lt $rgba.PixelWidth; $x++) {
        if ($pixels[$y * $stride + $x * 4 + 3] -gt 128) {
            $left = [Math]::Min($left,$x); $right = [Math]::Max($right,$x)
            $top = [Math]::Min($top,$y); $bottom = [Math]::Max($bottom,$y)
        }
    }
}
if ($right -le $left -or $bottom -le $top) { throw 'Source has no visible artwork.' }
$cropped = [Windows.Media.Imaging.CroppedBitmap]::new($source, [Windows.Int32Rect]::new($left,$top,$right-$left+1,$bottom-$top+1))
function Render-Icon([int]$Size) {
    $image = [Windows.Controls.Image]::new()
    $image.Source = $cropped
    $image.Stretch = [Windows.Media.Stretch]::Uniform
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($image,[Windows.Media.BitmapScalingMode]::HighQuality)
    $canvas = [Windows.Controls.Grid]::new()
    $canvas.Children.Add($image) | Out-Null
    $image.Margin = [Windows.Thickness]::new($Size * 0.02)
    $sizeValue = [Windows.Size]::new($Size,$Size)
    $canvas.Measure($sizeValue); $canvas.Arrange([Windows.Rect]::new($sizeValue)); $canvas.UpdateLayout()
    $target = [Windows.Media.Imaging.RenderTargetBitmap]::new($Size,$Size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $target.Render($canvas)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($target))
    $memory = [IO.MemoryStream]::new()
    try { $encoder.Save($memory); return ,$memory.ToArray() } finally { $memory.Dispose() }
}
[IO.File]::WriteAllBytes((Join-Path $root 'Resources\AppIcon.png'), (Render-Icon 1024))
[IO.File]::WriteAllBytes((Join-Path $root 'Packaging\Win11ContextMenu\PDF icon.png'), (Render-Icon 256))
# PNG-compressed ICO frames, including 125% / 150% / 200% taskbar scales.
$sizes = @(16,20,24,32,40,48,64,96,128,256)
$frames = @($sizes | ForEach-Object { ,(Render-Icon $_) })
$stream = [IO.File]::Create((Join-Path $root 'PDF icon.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i=0; $i -lt $sizes.Count; $i++) {
        $dimension = $sizes[$i] % 256
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([UInt16]1); $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frames[$i].Length); $writer.Write([UInt32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }
Write-Host 'Exported 96%-coverage app PNG and multi-resolution taskbar ICO.'
