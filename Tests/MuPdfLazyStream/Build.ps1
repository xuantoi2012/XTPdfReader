# Builds lazyopen.exe against the MuPDF SDK that PyMuPDF ships (same setup as Tests/MuPdfNativeWorker).
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sdk = Join-Path $repo 'Tests\bin\mupdf-bench-deps\pymupdf\mupdf-devel'
$out = Join-Path $repo 'Tests\bin\lazy-open'
New-Item -ItemType Directory -Force $out | Out-Null
$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
$cmd = 'call "' + $vcvars + '" >nul && cl /nologo /O2 /EHsc /MD /std:c++17 /I"' + (Join-Path $sdk 'include') + '" "' + (Join-Path $PSScriptRoot 'lazyopen.cpp') + '" /Fo"' + $out + '\lazyopen.obj" "' + (Join-Path $sdk 'lib\mupdfcpp64.lib') + '" /link /OUT:"' + $out + '\lazyopen.exe"'
& $env:ComSpec /d /s /c $cmd
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
Copy-Item (Join-Path $repo 'Tests\bin\mupdf-bench-deps\pymupdf\mupdfcpp64.dll') $out -Force
"built: $out\lazyopen.exe"
