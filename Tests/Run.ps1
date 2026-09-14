param([string]$BaselineRef = 'c08f4e2808929517cf6dc27066f6c0dcab0bbd14')
$ErrorActionPreference = 'Stop'
$testRoot = $PSScriptRoot
$appRoot = Split-Path -Parent $testRoot
New-Item -ItemType Directory -Force -Path (Join-Path $testRoot 'obj') | Out-Null
# Snapshot of the service before the optimization, run in a separate process so PDFium
# initialization, document state and allocation counters do not contaminate each other.
$baseline = git -C $appRoot show "${BaselineRef}:XTPdfMergeApp/Services/PdfThumbnailService.cs"
if ($LASTEXITCODE -ne 0) { throw 'Cannot read baseline service from git HEAD.' }
$baseline = ($baseline -join "`n").Replace('namespace XTPdfMergeApp.Services', 'namespace Baseline')
[IO.File]::WriteAllText((Join-Path $testRoot 'obj/BaselinePdfThumbnailService.cs'), $baseline)
dotnet build (Join-Path $testRoot 'PerformanceTests.csproj') -c Release -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
$testDll = Join-Path $testRoot 'bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.PerformanceTests.dll'
dotnet $testDll --baseline
if ($LASTEXITCODE -ne 0) { throw 'Baseline benchmark failed.' }
dotnet $testDll
if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
