# Builds against the MuPDF SDK shipped with PyMuPDF. Python is a build/test dependency only.
param([string]$Sdk = $env:XTPDF_MUPDF_SDK)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Sdk) { $Sdk = Join-Path $repo 'Tests\bin\mupdf-bench-deps\pymupdf\mupdf-devel' }
$runtimeDll = Join-Path (Split-Path -Parent $Sdk) 'mupdfcpp64.dll'
foreach ($required in (Join-Path $Sdk 'include\mupdf\fitz.h'), (Join-Path $Sdk 'lib\mupdfcpp64.lib'), $runtimeDll) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Missing MuPDF SDK file: $required. See Native/XtMuPdfWorker/README.md." }
}
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio C++ Build Tools (x64).' }
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio C++ x64 tools were not found.' }
$vcvars = Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat'
$cmd = 'call "' + $vcvars + '" >nul 2>&1 && cl /nologo /O2 /EHsc /MD /std:c++17 /W3 /DUNICODE /D_UNICODE /I"' + (Join-Path $sdk 'include') + '" "' + (Join-Path $PSScriptRoot 'worker.cpp') + '" /Fo"' + $out + '\worker.obj" "' + (Join-Path $sdk 'lib\mupdfcpp64.lib') + '" advapi32.lib /link /OUT:"' + $out + '\xtpdfworker.exe"'
& $env:ComSpec /d /s /c $cmd
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
Copy-Item -LiteralPath $runtimeDll -Destination $out -Force
"built: $out\xtpdfworker.exe"
