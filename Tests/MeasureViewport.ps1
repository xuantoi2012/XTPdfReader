param(
    [Parameter(Mandatory = $true)][string]$PdfPath,
    [ValidateRange(1, 1000000)][int]$Page = 1
)
$ErrorActionPreference = 'Stop'
$resolvedPdf = (Resolve-Path -LiteralPath $PdfPath).Path
$testDll = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.PerformanceTests.dll'
if (!(Test-Path -LiteralPath $testDll)) { throw 'Run Tests/Run.ps1 first to build the benchmark.' }
dotnet $testDll --viewport-pdf $resolvedPdf --page $Page
if ($LASTEXITCODE -ne 0) { throw 'Viewport benchmark failed.' }
