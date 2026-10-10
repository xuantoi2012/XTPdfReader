<#
.SYNOPSIS
  Dong goi PDF Reader Pro thanh 1 file PDFReaderPro-Setup.exe (cai vao Program Files, can quyen admin).

.DESCRIPTION
  1) dotnet publish Reader + XT Capture (framework-dependent, win-x64) vao _publish
  2) Kiem tra native worker + tessdata (them -WithPython de kem runtime va bytecode cu)
  3) nen _publish thanh zip, them __setup.json (phien ban)
  4) dotnet publish Setup\PdfReaderSetup.csproj (installer, 1 file nho)
  5) ghep: PDFReaderPro-Setup.exe = installer + zip + do dai zip (8 byte) + dau hieu (16 byte)  -> xem Setup\Payload.cs

  Khong con Velopack. Cap nhat: app tim ban moi tren GitHub Releases cua repo PDFReaderPro-Releases (tag v<Version>, file dinh kem
  PDFReaderPro-Setup.exe) - xem Services\AppUpdateService.cs.

.PARAMETER Version
  Phien ban phat hanh, kieu "1.0.1" (3 so). Ghi vao AssemblyVersion cua Reader va __setup.json.

.PARAMETER AllowUnlicensed
  Cho phep dong goi khi Licensing\LicenseConfig.cs chua co SupabaseUrl/AnonKey (ban noi bo, khong yeu cau license).

.PARAMETER WithPython
  Kem runtime Python du phong. Mac dinh chi dong goi native worker.

.EXAMPLE
  .\Build-Release.ps1 -Version 1.0.1 -AllowUnlicensed
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [switch]$AllowUnlicensed,
    [switch]$WithPython
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # .../XTPdfReader
$csproj = Join-Path $root "XTPdfMergeApp.csproj"
$publishDir = Join-Path $PSScriptRoot "_publish"
$setupDir = Join-Path $PSScriptRoot "_setup"
$releaseDir = Join-Path $PSScriptRoot "_release"
$zipPath = Join-Path $PSScriptRoot "_payload.zip"

Write-Host "== PDF Reader Pro - dong goi ban $Version ==" -ForegroundColor Cyan
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version phai co dang 1.2.3." }

# 0) Ban phat hanh phai bat license: neu chua dien URL/anon key cua Supabase thi app chay khong can license.
$licenseConfig = Get-Content -Raw (Join-Path $root "Licensing\LicenseConfig.cs")
if ($licenseConfig -match 'SupabaseUrl\s*=\s*""' -or $licenseConfig -match 'AnonKey\s*=\s*""') {
    if (-not $AllowUnlicensed) { throw "Licensing/LicenseConfig.cs chua co SupabaseUrl/AnonKey - ban nay se chay khong can license. Xem Licensing/Server/README.md, hoac them -AllowUnlicensed cho ban noi bo." }
    Write-Host "  (-AllowUnlicensed: ban noi bo, KHONG co license.)" -ForegroundColor DarkYellow
}

# 1) Publish Reader + XT Capture vao CUNG thu muc. Khong kem .NET: may khach can .NET 10 Desktop Runtime.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Write-Host "`n[1/5] dotnet publish Reader + XT Capture..." -ForegroundColor Yellow
$nativeOnly = if ($WithPython) { 'false' } else { 'true' }
dotnet publish $csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:DeployToBundle=false -p:NativeOnly=$nativeOnly -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish that bai." }
$captureProj = Join-Path $root "XTCapture\XTCapture.csproj"
dotnet publish $captureProj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish XT Capture that bai." }
foreach ($required in "XTPdfMergeApp.exe", "XTCapture.exe") {
    if (-not (Test-Path (Join-Path $publishDir $required))) { throw "$required khong co trong ban publish." }
}

# 2) Worker .py -> bytecode (cung ten file), de bo cai khong chua ma nguon Python doc duoc.
if ($WithPython) {
    Write-Host "`n[2/5] bytecode cho worker..." -ForegroundColor Yellow
    & (Join-Path $PSScriptRoot "Protect-Scripts.ps1") -PublishDir $publishDir
} else {
    Write-Host "`n[2/5] native worker (khong kem Python)..." -ForegroundColor Yellow
    $needed = @('xtpdfworker.exe', 'tessdata\vie.traineddata')
    # The statically linked worker (xtpdfworker.static) carries MuPDF; the other one needs its DLL.
    if (-not (Test-Path -LiteralPath (Join-Path $publishDir 'xtpdfworker.static'))) { $needed += 'mupdfcpp64.dll' }
    foreach ($required in $needed) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDir $required))) { throw "$required is missing from the native publish." }
    }
}

# 3) Zip payload.
Write-Host "`n[3/5] nen payload..." -ForegroundColor Yellow
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
[IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Update)
try {
    $entry = $zip.CreateEntry("__setup.json")
    $writer = New-Object IO.StreamWriter($entry.Open())
    $writer.Write("{`"version`":`"$Version`"}")
    $writer.Dispose()
} finally { $zip.Dispose() }

# 4) Installer.
Write-Host "`n[4/5] dotnet publish installer..." -ForegroundColor Yellow
if (Test-Path $setupDir) { Remove-Item $setupDir -Recurse -Force }
dotnet publish (Join-Path $root "Setup\PdfReaderSetup.csproj") -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:Version=$Version -o $setupDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish installer that bai." }
$stub = Join-Path $setupDir "PDFReaderPro-Setup.exe"
if (-not (Test-Path $stub)) { throw "Khong thay PDFReaderPro-Setup.exe sau khi publish installer." }

# 5) Ghep: [installer][zip][do dai zip int64][XTPDFRSETUPV1ZIP]
Write-Host "`n[5/5] ghep installer + payload..." -ForegroundColor Yellow
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }
New-Item -ItemType Directory $releaseDir | Out-Null
$setupExe = Join-Path $releaseDir "PDFReaderPro-Setup.exe"
Copy-Item $stub $setupExe
$zipBytes = [IO.File]::ReadAllBytes($zipPath)
$out = [IO.File]::Open($setupExe, [IO.FileMode]::Append, [IO.FileAccess]::Write)
try {
    $out.Write($zipBytes, 0, $zipBytes.Length)
    $length = [BitConverter]::GetBytes([int64]$zipBytes.Length)
    $out.Write($length, 0, $length.Length)
    $magic = [Text.Encoding]::ASCII.GetBytes("XTPDFRSETUPV1ZIP")
    $out.Write($magic, 0, $magic.Length)
} finally { $out.Dispose() }
Remove-Item $zipPath -Force

$sizeMb = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
Write-Host "`n== Xong =="  -ForegroundColor Green
Write-Host "Setup.exe : $setupExe ($sizeMb MB)"
Write-Host "Dua len GitHub: tao Release voi tag v$Version tren repo PDFReaderPro-Releases, dinh kem dung file PDFReaderPro-Setup.exe (app tim file theo ten nay)." -ForegroundColor Cyan
