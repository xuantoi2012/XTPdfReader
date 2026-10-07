$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $repo
try {
    & dotnet build VirtualPrinter\Service -v minimal
    if ($LASTEXITCODE) { throw 'Service build failed' }
    if (!(Test-Path -LiteralPath Tests\bin\virtual-printer\phase0\word-120-headings.pdf)) {
        $probePython = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
        $probeOutput = Join-Path $repo 'Tests\bin\virtual-printer\phase0'
        & $probePython Tests\VirtualPrinter\Generate-WordSpike.py $probeOutput
        if ($LASTEXITCODE) { throw 'Fixture generation failed' }
        & Tests\VirtualPrinter\Export-WordSpike.ps1 -OutputDirectory $probeOutput
    }
    & dotnet run --project Tests\VirtualPrinter\ServiceTests -- $repo
    if ($LASTEXITCODE) { throw 'Service tests failed' }
    $latest = Get-ChildItem Tests\bin\virtual-printer\service -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    & .\bin\MuPdfRuntime\python.exe Tests\VirtualPrinter\Verify-ServiceOutput.py (Join-Path $latest.FullName 'results.json') Tests\bin\virtual-printer\phase0\word-120-headings.pdf
    if ($LASTEXITCODE) { throw 'Output verification failed' }
} finally { Pop-Location }
