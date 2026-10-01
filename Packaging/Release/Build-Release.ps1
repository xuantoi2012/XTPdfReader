<#
.SYNOPSIS
  Dong goi PDF Reader Pro thanh 1 Setup.exe (Velopack) san sang phat hanh.

.DESCRIPTION
  Lam 3 buoc: dotnet publish (self-contained win-x64) -> vpk pack (sinh Setup.exe + cac file
  release khac) -> in ra duong dan ket qua. Khong tu upload len dau ca - xem README.md canh day
  de biet buoc cuoi (dua len GitHub Releases).

.PARAMETER Version
  Phien ban phat hanh, kieu SemVer (vd "1.0.1"). Ghi vao ca .csproj (AssemblyVersion) lan goi cho
  vpk (--packVersion) de 2 cho khop nhau.

.PARAMETER SignToolParams
  Tuy chon. Tham so truyen thang cho signtool.exe (vd '/a /fd sha256 /t http://timestamp.digicert.com').
  Bo qua = file KHONG duoc ky so (Windows SmartScreen se canh bao "Unknown publisher" voi nguoi
  cai lan dau). Xem README.md phan "Ky so" de biet cach co chung chi.

.EXAMPLE
  .\Build-Release.ps1 -Version 1.0.1
  .\Build-Release.ps1 -Version 1.0.1 -SignToolParams '/a /fd sha256 /t http://timestamp.digicert.com'
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$SignToolParams = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # .../XTPdfReader
$csproj = Join-Path $root "XTPdfMergeApp.csproj"
$icon = Join-Path $root "PDF icon.ico"
$publishDir = Join-Path $PSScriptRoot "_publish"
$releaseDir = Join-Path $PSScriptRoot "_release"

Write-Host "== PDF Reader Pro - dong goi ban $Version ==" -ForegroundColor Cyan

# 1) Publish self-contained win-x64, dung Version truyen vao cho khop AssemblyVersion trong file .exe.
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Write-Host "`n[1/2] dotnet publish..." -ForegroundColor Yellow
dotnet publish $csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:DeployToBundle=false -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish that bai." }

# 2) vpk pack: sinh Setup.exe + portable zip + file manifest release (releases.win.json...).
Write-Host "`n[2/2] vpk pack..." -ForegroundColor Yellow
if (Test-Path $releaseDir) { Remove-Item $releaseDir -Recurse -Force }

$vpkArgs = @(
    "pack",
    "--packId", "PDFReaderPro",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--mainExe", "XTPdfMergeApp.exe",
    "--packTitle", "PDF Reader Pro",
    "--packAuthors", "XT",
    "--outputDir", $releaseDir,
    "--icon", $icon,
    "--splashImage", (Join-Path $PSScriptRoot "splash.png"),
    "--splashProgressColor", "#F2994A",
    "--runtime", "win-x64"
)
if ($SignToolParams) {
    $vpkArgs += @("--signParams", $SignToolParams)
} else {
    Write-Host "  (Khong co -SignToolParams - ban cai se KHONG duoc ky so.)" -ForegroundColor DarkYellow
}

& vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk pack that bai." }

$setupExe = Join-Path $releaseDir "PDFReaderPro-win-Setup.exe"
Write-Host "`n== Xong =="  -ForegroundColor Green
Write-Host "Setup.exe : $setupExe"
Write-Host "Thu muc release day du (can upload CA thu muc nay len GitHub Release, khong chi 1 file Setup.exe): $releaseDir"
Write-Host "`nBuoc tiep theo: xem README.md canh file nay de dua len GitHub Releases." -ForegroundColor Cyan
