param([string]$BaselineRef = '', [string]$MuPdfSample = '')
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
# The reader ships with the MuPDF-only engine (MuPdfOnly=true, default since 2026-10-06). The big PDFium regression suite is engine-specific, so it is built and run
# with -p:MuPdfOnly=false (pass A). The engine-neutral checks (layers, sheets, history, dialogs...) and the MuPDF checks run on the production build (pass B).
$csproj = Join-Path $testRoot 'PerformanceTests.csproj'
$pdfiumOut = Join-Path $testRoot 'bin/pdfium-suite'
$muOut = Join-Path $testRoot 'bin/mupdf-suite'

Write-Host '== Pass A: PDFium regression suite (MuPdfOnly=false)'
dotnet build $csproj -c Release -v quiet -p:MuPdfOnly=false -o $pdfiumOut
if ($LASTEXITCODE -ne 0) { throw 'PDFium test build failed.' }
$pdfiumDll = Join-Path $pdfiumOut 'XTPdfMergeApp.PerformanceTests.dll'
if ($hasBaseline) {
    dotnet $pdfiumDll --baseline
    if ($LASTEXITCODE -ne 0) { throw 'Baseline benchmark failed.' }
}
dotnet $pdfiumDll
if ($LASTEXITCODE -ne 0) { throw 'PDFium regression checks failed.' }

Write-Host '== Pass B: production build (MuPDF only): engine-neutral checks, UI smoke, MuPDF checks'
dotnet build $csproj -c Release -v quiet -o $muOut
if ($LASTEXITCODE -ne 0) { throw 'MuPDF test build failed.' }
$muDll = Join-Path $muOut 'XTPdfMergeApp.PerformanceTests.dll'
dotnet $muDll --layer-merge-only
if ($LASTEXITCODE -ne 0) { throw 'Engine-neutral checks failed.' }
dotnet $muDll --ui-smoke
if ($LASTEXITCODE -ne 0) { throw 'UI smoke checks failed.' }
if ($MuPdfSample -ne '') {
    dotnet $muDll --mupdf-viewport-check $MuPdfSample
    if ($LASTEXITCODE -ne 0) { throw 'MuPDF viewport checks failed.' }
    dotnet $muDll --mupdf-throughput-viewer-check $MuPdfSample
    if ($LASTEXITCODE -ne 0) { throw 'MuPDF throughput viewer checks failed.' }
}
else { Write-Warning 'No -MuPdfSample supplied: the MuPDF viewport checks were skipped (give any multi-page PDF).' }
