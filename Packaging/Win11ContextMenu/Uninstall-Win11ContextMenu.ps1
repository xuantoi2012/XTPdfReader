[CmdletBinding()]
param(
    [switch]$RestartExplorer,
    [switch]$RemoveDevCertificate
)

$ErrorActionPreference = "Stop"

$packageName = "XTPdfMergeApp.ContextMenu"
$publisher = "CN=XTPdfMergeApp"

$packages = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($packages) {
    $packages | Remove-AppxPackage
    Write-Host "Removed package: $packageName"
}
else {
    Write-Host "Package not installed: $packageName"
}

if ($RemoveDevCertificate) {
    Get-ChildItem Cert:\CurrentUser\TrustedPeople, Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $publisher } |
        Remove-Item -Force
    Write-Host "Removed local dev certificates for $publisher"
}

if ($RestartExplorer) {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
}
