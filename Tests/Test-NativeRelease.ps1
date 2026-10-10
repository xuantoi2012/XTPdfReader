param(
    [string]$RealPdf,
    [switch]$AllPages
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $PSScriptRoot 'bin/NativeReleaseGate'
$logs = Join-Path $output 'validation'
New-Item -ItemType Directory -Path $logs -Force | Out-Null

function Invoke-Check([string]$Name, [string]$Executable, [string[]]$Arguments) {
    Write-Host "Checking $Name"
    $log = Join-Path $logs "$Name.log"
    & $Executable @Arguments *> $log
    $code = $LASTEXITCODE
    Get-Content -LiteralPath $log -Tail 8 | Write-Host
    if ($code -ne 0) { throw "$Name failed (exit $code). See $log" }
}

Push-Location $repo
try {
    Invoke-Check 'build' 'dotnet' @('build', 'Tests/PerformanceTests.csproj', '-c', 'Release', '-o', $output, '-v', 'quiet')
    $tests = Join-Path $output 'XTPdfMergeApp.PerformanceTests.dll'
    Invoke-Check 'native-integration' 'dotnet' @($tests, '--test', 'TestNativeOnlyInstallation,TestNativeMetadataAsync,TestNativeSharedTransportAsync,TestNativeRenderTimingsAsync,TestOcrEndToEnd')
    foreach ($name in @('cache', 'cancel', 'lifetime', 'edit')) {
        Invoke-Check "native-$name" 'python' @("Native/XtMuPdfWorker/test_$name.py")
    }
    Invoke-Check 'native-ocr' 'python' @('Native/XtMuPdfWorker/test_ocr.py', (Join-Path $output 'results/ocr/scan.pdf'))
    Invoke-Check 'migration' 'dotnet' @($tests, '--mupdf-migration-check')
    Invoke-Check 'reader-memory' 'dotnet' @($tests, '--reader-memory-regression')
    Invoke-Check 'reader-binding' 'dotnet' @($tests, '--reader-binding-check')
    Invoke-Check 'ui-smoke' 'dotnet' @($tests, '--ui-smoke')
    if ($RealPdf) {
        $mode = if ($AllPages) { '--reader-navigation-check' } else { '--reader-open-check' }
        Invoke-Check 'real-reader' 'dotnet' @($tests, $mode, $RealPdf)
    }
    Write-Host "PASS: native release validation. Logs: $logs"
}
finally { Pop-Location }
