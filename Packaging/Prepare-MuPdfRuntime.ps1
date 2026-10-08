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

# Prune what the workers never import (the full runtime is ~99 MB, this leaves ~62 MB). Checked by importing pymupdf + the
# modules the three workers use, and by running OcrWorker on a sample page. Re-run that check when PyMuPDF or Python is upgraded.
foreach ($rel in @('Lib\idlelib', 'Lib\ensurepip', 'Lib\venv', 'Lib\turtledemo', 'Lib\tkinter', 'Lib\test', 'Lib\lib2to3', 'Lib\distutils',
                   'Lib\pydoc_data', 'Lib\sqlite3', 'Lib\tomllib', 'Lib\turtle.py', 'Lib\site-packages\pymupdf\mupdf-devel')) {
    Remove-Item -LiteralPath (Join-Path $Output $rel) -Recurse -Force -ErrorAction SilentlyContinue
}
Get-ChildItem -LiteralPath $Output -Recurse -Directory -Force |
    Where-Object { $_.Name -eq '__pycache__' -or (($_.Name -in 'test', 'tests') -and $_.FullName -notmatch 'site-packages') } |
    Sort-Object { $_.FullName.Length } -Descending |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
foreach ($name in @('_tkinter.pyd', 'tcl86t.dll', 'tk86t.dll', 'winsound.pyd', '_msi.pyd', '_sqlite3.pyd', 'sqlite3.dll', 'libcrypto-3-x64.dll',
                    'libssl-3-x64.dll', '_ssl.pyd', '_hashlib.pyd', '_asyncio.pyd')) {
    Remove-Item -LiteralPath (Join-Path $Output "DLLs\$name") -Force -ErrorAction SilentlyContinue
}
Get-ChildItem -LiteralPath (Join-Path $Output 'DLLs') -Filter '_test*.pyd' | Remove-Item -Force   # _socket.pyd stays: pymupdf imports socket
$env:PYTHONDONTWRITEBYTECODE = '1'
& (Join-Path $Output 'python.exe') -c "import pymupdf, json, math, re, time, sys, os, traceback, multiprocessing, winreg, hashlib, socket, tempfile, subprocess"
if ($LASTEXITCODE -ne 0) { throw 'The pruned runtime can no longer import what the workers need.' }
# Ship bytecode only (stdlib + PyMuPDF): without it every worker start compiles the sources (~1 s slower), and the .py files are not needed.
# Measured 2026-10-08: ~145 ms to import pymupdf, 59 MB in total.
Get-ChildItem -LiteralPath $Output -Recurse -Directory -Filter '__pycache__' | Remove-Item -Recurse -Force
& (Join-Path $Output 'python.exe') -m compileall -q -b -j 0 -o 2 (Join-Path $Output 'Lib')
if ($LASTEXITCODE -ne 0) { throw 'Compiling the runtime to bytecode failed.' }
foreach ($source in Get-ChildItem -LiteralPath (Join-Path $Output 'Lib') -Recurse -Filter '*.py') {
    if (Test-Path -LiteralPath ($source.FullName + 'c')) { Remove-Item -LiteralPath $source.FullName }
}
& (Join-Path $Output 'python.exe') -c "import pymupdf, multiprocessing, winreg"
if ($LASTEXITCODE -ne 0) { throw 'The bytecode-only runtime can no longer import what the workers need.' }
Write-Output "Pruned runtime: $([math]::Round((Get-ChildItem -LiteralPath $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)) MB"
