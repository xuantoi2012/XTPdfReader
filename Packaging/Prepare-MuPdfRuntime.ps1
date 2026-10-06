param(
    [string]$PythonSource = (Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python'),
    [string]$PackageSource,
    [string]$Output
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$PackageSource) { $PackageSource = Join-Path $repo 'Tests\bin\mupdf-bench-deps' }
if (!$Output) { $Output = Join-Path $repo 'bin\MuPdfRuntime' }
if (!(Test-Path -LiteralPath (Join-Path $PythonSource 'python.exe'))) { throw 'Supply a Python 3.12 runtime with -PythonSource.' }
if (!(Test-Path -LiteralPath (Join-Path $PackageSource 'pymupdf-1.28.2.dist-info'))) { throw 'Supply pinned PyMuPDF 1.28.2 packages with -PackageSource.' }
New-Item -ItemType Directory -Path (Join-Path $Output 'Lib\site-packages') -Force | Out-Null
foreach ($name in @('python.exe', 'python3.dll', 'python312.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'LICENSE.txt', 'DLLs')) {
    Copy-Item -LiteralPath (Join-Path $PythonSource $name) -Destination $Output -Recurse -Force
}
Get-ChildItem -LiteralPath (Join-Path $PythonSource 'Lib') | Where-Object Name -ne 'site-packages' | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $Output 'Lib') -Recurse -Force
}
foreach ($name in @('pymupdf', 'fitz', 'pymupdf-1.28.2.dist-info')) {
    Copy-Item -LiteralPath (Join-Path $PackageSource $name) -Destination (Join-Path $Output 'Lib\site-packages') -Recurse -Force
}
Write-Output "Prepared local MuPDF runtime: $Output"
