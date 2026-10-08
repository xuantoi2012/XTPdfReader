param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutputDirectory = ''
)
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) { $OutputDirectory = $PSScriptRoot }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File $PSCommandPath -Version $Version -OutputDirectory $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Splash generation failed.' }
    return
}
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot 'Branding-Xaml.ps1')
$iconUri = [Uri]::new((Join-Path $root 'Resources\AppIcon.png')).AbsoluteUri
$markup = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'InstallerSplash.xaml'))
$markup = $markup.Replace('__ICON__', $iconUri).Replace('__VERSION__', [Security.SecurityElement]::Escape($Version))
$markup = $markup.Replace('<!--AMBIENT-->', (Get-AmbientMarkup $root))
$surface = [Windows.Markup.XamlReader]::Parse($markup)
$size = [Windows.Size]::new(480, 390)
$surface.Measure($size)
$surface.Arrange([Windows.Rect]::new($size))
$surface.UpdateLayout()
$segment = $surface.FindName('ProgressSegment')
$gif = [Windows.Media.Imaging.GifBitmapEncoder]::new()
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
for ($i = 0; $i -lt 96; $i++) {
    # Ping-pong animation: continuous activity, never a simulated completion percentage.
    $phase = $i / 96.0
    Set-AmbientPhase $surface $phase
    $segment.RenderTransform.X = 262 * (0.5 - 0.5 * [Math]::Cos(6 * [Math]::PI * $phase))
    $surface.UpdateLayout()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(480, 390, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($surface)
    if ($i -eq 9) {
        $png = [Windows.Media.Imaging.PngBitmapEncoder]::new()
        $png.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $stream = [IO.File]::Create((Join-Path $OutputDirectory 'splash.png'))
        try { $png.Save($stream) } finally { $stream.Dispose() }
    }
    $metadata = [Windows.Media.Imaging.BitmapMetadata]::new('gif')
    $metadata.SetQuery('/grctlext/Delay', [UInt16]10)
    $metadata.SetQuery('/grctlext/Disposal', [byte]2)
    if ($i -eq 0) {
        $metadata.SetQuery('/appext/application', [Text.Encoding]::ASCII.GetBytes('NETSCAPE2.0'))
        $metadata.SetQuery('/appext/data', [byte[]](3, 1, 0, 0, 0))
    }
    $gif.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap, $null, $metadata, $null))
}
$stream = [IO.File]::Create((Join-Path $OutputDirectory 'splash.gif'))
try { $gif.Save($stream) } finally { $stream.Dispose() }
# WPF's GIF encoder drops frame delay/application metadata. Set these GIF89a
# control fields explicitly so every decoder loops at the same 100 ms cadence.
$gifPath = Join-Path $OutputDirectory 'splash.gif'
$bytes = [IO.File]::ReadAllBytes($gifPath)
$offset = 13
if ($bytes[10] -band 128) { $offset += 3 * [Math]::Pow(2, (($bytes[10] -band 7) + 1)) }
$loopOffset = $offset
while ($offset -lt $bytes.Length) {
    $block = $bytes[$offset++]
    if ($block -eq 59) { break }
    if ($block -eq 33) {
        $label = $bytes[$offset++]
        if ($label -eq 249) {
            $bytes[$offset + 2] = 10
            $bytes[$offset + 3] = 0
        }
    } elseif ($block -eq 44) {
        $packed = $bytes[$offset + 8]
        $offset += 9
        if ($packed -band 128) { $offset += 3 * [Math]::Pow(2, (($packed -band 7) + 1)) }
        $offset++ # LZW minimum code size
    } else { throw "Unexpected GIF block $block" }
    do {
        $length = $bytes[$offset++]
        $offset += $length
    } while ($length -ne 0)
}
$loop = [byte[]](33,255,11) + [Text.Encoding]::ASCII.GetBytes('NETSCAPE2.0') + [byte[]](3,1,0,0,0)
$stream = [IO.File]::Create($gifPath)
try {
    $stream.Write($bytes, 0, $loopOffset)
    $stream.Write($loop, 0, $loop.Length)
    $stream.Write($bytes, $loopOffset, $bytes.Length - $loopOffset)
} finally { $stream.Dispose() }
Write-Host "Splash generated: PDF Reader Pro v$Version (480 x 390)"
