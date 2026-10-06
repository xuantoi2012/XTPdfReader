param([int]$Seconds = 35)
$ErrorActionPreference = 'Stop'
if ($Seconds -lt 10 -or $Seconds -gt 120) { throw 'Seconds must be 10..120.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in an elevated PowerShell session to collect CPU stacks.'
}
$directory = Join-Path $PSScriptRoot '..\bin\foxit-study'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$path = Join-Path $directory ('foxit-cpu-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.etl')
$statusPath = Join-Path $directory 'capture-status.json'
$logPath = Join-Path $directory 'capture-wpr.log'
function Write-CaptureStatus([string]$State, [string]$ErrorMessage = '') {
    [pscustomobject]@{
        State = $State
        Utc = [DateTime]::UtcNow.ToString('o')
        Trace = $path
        Seconds = $Seconds
        Error = $ErrorMessage
    } | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding UTF8
}
# Do not cancel an existing recording. WPR start must succeed before we own it.
Write-CaptureStatus 'Starting'
& wpr.exe -start CPU -filemode *> $logPath
if ($LASTEXITCODE -ne 0) {
    Write-CaptureStatus 'Failed' "WPR start exit code $LASTEXITCODE; see capture-wpr.log."
    throw 'WPR could not start; an existing recording may be active.'
}
Write-CaptureStatus 'Recording'
try {
    Write-Host "Recording $Seconds seconds. In Foxit: zoom in, zoom out, revisit the same zoom."
    Start-Sleep -Seconds $Seconds
} finally {
    Write-CaptureStatus 'Stopping'
    & wpr.exe -stop $path *>> $logPath
    if ($LASTEXITCODE -ne 0) {
        Write-CaptureStatus 'Failed' "WPR stop exit code $LASTEXITCODE; inspect wpr -status."
        Write-Warning 'WPR stop failed; inspect wpr -status.'
    } else { Write-CaptureStatus 'Completed' }
}
Write-Output "Trace: $path"
