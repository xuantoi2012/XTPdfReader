# Builds xtmupdfworker.exe against the MuPDF SDK that PyMuPDF ships (same setup as Tests/MuPdfNativeWorker).
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sdk = Join-Path $repo 'Tests\bin\mupdf-bench-deps\pymupdf\mupdf-devel'
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Force $out | Out-Null
$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
$cmd = 'call "' + $vcvars + '" >nul 2>&1 && cl /nologo /O2 /EHsc /MD /std:c++17 /W3 /DUNICODE /D_UNICODE /I"' + (Join-Path $sdk 'include') + '" "' + (Join-Path $PSScriptRoot 'worker.cpp') + '" /Fo"' + $out + '\worker.obj" "' + (Join-Path $sdk 'lib\mupdfcpp64.lib') + '" advapi32.lib /link /OUT:"' + $out + '\xtmupdfworker.exe"'
& $env:ComSpec /d /s /c $cmd
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
Copy-Item (Join-Path $repo 'Tests\bin\mupdf-bench-deps\pymupdf\mupdfcpp64.dll') $out -Force
"built: $out\xtmupdfworker.exe"
