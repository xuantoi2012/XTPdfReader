param([switch]$Check)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$source = 'T:\01-Phong-Ban-XN\04-XN TK Ha Tang\05-Tran Van Thoai\15.Project\07-THIEN PHUC VINH HANG VIEN\ACAD\CIVIL\3.TNM\01A.MB THOAT NUOC MUA - FIT_ghep.pdf'
$python = 'C:\Users\Admin\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
$names = @('XTPDF_EXPERIMENTAL_ENGINE', 'XTPDF_MUPDF_SOURCE', 'XTPDF_MUPDF_PYTHON', 'XTPDF_MUPDF_WORKER', 'PYTHONPATH')
$saved = @{}
foreach ($name in $names) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:XTPDF_EXPERIMENTAL_ENGINE = 'mupdf'
    $env:XTPDF_MUPDF_SOURCE = $source
    $env:XTPDF_MUPDF_PYTHON = $python
    $env:XTPDF_MUPDF_WORKER = Join-Path $PSScriptRoot 'MuPdfViewportWorker.py'
    $env:PYTHONPATH = Join-Path $repo 'Tests\bin\mupdf-bench-deps'
    if (!(Test-Path -LiteralPath (Join-Path $env:PYTHONPATH 'pymupdf'))) { throw 'Install isolated benchmark dependencies first.' }
    if ($Check) {
        & (Join-Path $repo 'Tests\bin\Debug\net10.0-windows10.0.19041.0\XTPdfMergeApp.PerformanceTests.exe') --mupdf-viewport-check $source
        if ($LASTEXITCODE -ne 0) { throw 'MuPDF viewport checks failed.' }
    } else {
        if (Get-Process -Name XTPdfMergeApp -ErrorAction SilentlyContinue) {
            throw 'Close the existing reader manually before launching the trial; single-instance forwarding would keep its PDFium settings.'
        }
        # User launches this interactive trial explicitly; no existing window is automated.
        Start-Process -FilePath (Join-Path $repo 'bin\Debug\net10.0-windows10.0.19041.0\XTPdfMergeApp.exe') -ArgumentList ('"' + $source + '"') -PassThru
    }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
