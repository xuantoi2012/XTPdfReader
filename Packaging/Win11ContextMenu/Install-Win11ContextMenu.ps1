[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$AppDir = "",
    [switch]$SkipPublish,
    [switch]$RestartExplorer
)

$ErrorActionPreference = "Stop"

$packageName = "XTPdfMergeApp.ContextMenu"
$publisher = "CN=XTPdfMergeApp"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir "..\..")
$projectPath = Join-Path $repoRoot "XTPdfMergeApp.csproj"
$stagingDir = Join-Path $repoRoot "bin\win11-context-menu"
$publishDir = Join-Path $repoRoot "bin\win11-context-menu-publish"
$packagePath = Join-Path $stagingDir "$packageName.msix"

function Find-WindowsSdkTool([string]$toolName) {
    $cmd = Get-Command $toolName -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $kitsRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (-not (Test-Path $kitsRoot)) {
        throw "Windows SDK not found. Install Windows 10/11 SDK to get $toolName."
    }

    $candidate = Get-ChildItem $kitsRoot -Directory |
        Sort-Object Name -Descending |
        ForEach-Object {
            Join-Path $_.FullName "x64\$toolName"
        } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1

    if (-not $candidate) {
        throw "$toolName not found under $kitsRoot. Install Windows 10/11 SDK."
    }

    return $candidate
}

New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

if (-not $SkipPublish) {
    dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained false -o $publishDir
    $AppDir = $publishDir
}

if ([string]::IsNullOrWhiteSpace($AppDir)) {
    throw "AppDir is required when using -SkipPublish."
}

$AppDir = (Resolve-Path $AppDir).Path
$exePath = Join-Path $AppDir "XTPdfMergeApp.exe"
if (-not (Test-Path $exePath)) {
    throw "XTPdfMergeApp.exe not found in AppDir: $AppDir"
}

$makeAppx = Find-WindowsSdkTool "MakeAppx.exe"
$signTool = Find-WindowsSdkTool "SignTool.exe"

if (Test-Path $packagePath) {
    Remove-Item -LiteralPath $packagePath -Force
}

& $makeAppx pack /o /d $scriptDir /nv /p $packagePath
if ($LASTEXITCODE -ne 0) { throw "MakeAppx failed with exit code $LASTEXITCODE." }

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $publisher -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $publisher `
        -FriendlyName "XTPdfMergeApp sparse package signing" `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -NotAfter (Get-Date).AddYears(5)
}

$trusted = Get-ChildItem Cert:\CurrentUser\TrustedPeople |
    Where-Object { $_.Thumbprint -eq $cert.Thumbprint } |
    Select-Object -First 1

if (-not $trusted) {
    $cerPath = Join-Path $stagingDir "$packageName.cer"
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
    Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
}

& $signTool sign /fd SHA256 /sha1 $cert.Thumbprint $packagePath
if ($LASTEXITCODE -ne 0) { throw "SignTool failed with exit code $LASTEXITCODE." }

$existing = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($existing) {
    $existing | Remove-AppxPackage
}

Add-AppxPackage -Path $packagePath -ExternalLocation $AppDir

if ($RestartExplorer) {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
}

Write-Host "Installed Windows 11 context menu package: $packageName"
Write-Host "External app location: $AppDir"
