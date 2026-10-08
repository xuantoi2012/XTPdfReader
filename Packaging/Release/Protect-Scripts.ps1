<#
.SYNOPSIS
  Replaces the worker .py files of a publish folder by compiled bytecode (same file names), so the installer does not carry readable source.
.DESCRIPTION
  CPython runs a file that starts with the bytecode magic number even if it is called .py, so the C# side needs no change.
  This only keeps casual readers out (bytecode can be decompiled); it is not encryption. Uses the runtime's own python.exe so the
  bytecode version matches. -OptimizeLevel 2 also drops docstrings and asserts.
#>
param([Parameter(Mandatory = $true)][string]$PublishDir)
$ErrorActionPreference = 'Stop'
$python = Join-Path $PublishDir 'MuPdfRuntime\python.exe'
if (!(Test-Path -LiteralPath $python)) { throw "No embedded python in $PublishDir" }
$env:PYTHONDONTWRITEBYTECODE = '1'
foreach ($name in 'MuPdfWorker.py', 'OcrWorker.py', 'TextEditWorker.py') {
    $path = Join-Path $PublishDir $name
    if (!(Test-Path -LiteralPath $path)) { throw "$name is missing from the publish folder." }
    $tmp = "$path.compiled"
    & $python -c "import py_compile,sys; py_compile.compile(sys.argv[1], cfile=sys.argv[2], doraise=True, optimize=2)" $path $tmp
    if ($LASTEXITCODE -ne 0) { throw "Could not compile $name." }
    Move-Item -LiteralPath $tmp -Destination $path -Force
}
Write-Host 'Worker scripts compiled to bytecode.'
