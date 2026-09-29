param([string]$BaselineRef = '')
$ErrorActionPreference = 'Stop'
$testRoot = $PSScriptRoot
$appRoot = Split-Path -Parent $testRoot
New-Item -ItemType Directory -Force -Path (Join-Path $testRoot 'obj') | Out-Null
$baselineSource = Join-Path $testRoot 'obj/BaselinePdfThumbnailService.cs'
Remove-Item -LiteralPath $baselineSource -Force -ErrorAction SilentlyContinue

# A baseline is optional: shallow/standalone checkouts do not necessarily contain the
# historical optimization commit. Current regression checks must remain runnable there.
$hasBaseline = -not [string]::IsNullOrWhiteSpace($BaselineRef)
if ($hasBaseline) {
    $baseline = git -C $appRoot show "${BaselineRef}:Services/PdfThumbnailService.cs"
    if ($LASTEXITCODE -ne 0) { throw "Cannot read baseline service from $BaselineRef." }
    $baseline = ($baseline -join "`n").Replace('namespace XTPdfMergeApp.Services', 'namespace Baseline')
    [IO.File]::WriteAllText($baselineSource, $baseline)
}
else {
    Write-Warning 'No -BaselineRef supplied; running current regression checks only.'
}
dotnet build (Join-Path $testRoot 'PerformanceTests.csproj') -c Release -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
$testDll = Join-Path $testRoot 'bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.PerformanceTests.dll'
if ($hasBaseline) {
    dotnet $testDll --baseline
    if ($LASTEXITCODE -ne 0) { throw 'Baseline benchmark failed.' }
}
dotnet $testDll
if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
