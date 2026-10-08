param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot '..\..\docs\design\implemented' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') {
    & powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File $PSCommandPath -OutputDirectory $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Branding preview failed.' }
    return
}
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $PSScriptRoot 'Branding-Xaml.ps1')
$iconUri = [Uri]::new((Join-Path $root 'Resources\AppIcon.png')).AbsoluteUri
$markup = [IO.File]::ReadAllText((Join-Path $root 'Controls\UpdateReadyWindow.xaml'))
# Load the actual UI without constructing an application or contacting the update server.
$markup = $markup -replace 'x:Class="[^"]*"', '' -replace 'Click="Dismiss_Click"', ''
$markup = $markup.Replace('{StaticResource App.Icon.Logo}', $iconUri)
$markup = $markup.Replace('<local:AmbientBackdrop Grid.RowSpan="3"/>', (Get-AmbientMarkup $root))
$window = [Windows.Markup.XamlReader]::Parse($markup)
$window.FindName('VersionLabel').Text = 'PDF Reader Pro v1.0.0'
$surface = $window.Content
$surface.Background = $window.Background
$size = [Windows.Size]::new(480,390)
$surface.Measure($size)
$surface.Arrange([Windows.Rect]::new($size))
$surface.UpdateLayout()
$bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(480,390,96,96,[Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($surface)
$png = [Windows.Media.Imaging.PngBitmapEncoder]::new()
$png.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$stream = [IO.File]::Create((Join-Path $OutputDirectory 'update-ready.png'))
try { $png.Save($stream) } finally { $stream.Dispose(); $window.Close() }
Write-Host "Preview saved: $OutputDirectory"
