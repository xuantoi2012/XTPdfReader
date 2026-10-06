$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sdk = Join-Path $repo 'Tests\bin\mupdf-bench-deps\pymupdf\mupdf-devel'
$output = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$vcvars = 'C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat'
$cpp = Join-Path $PSScriptRoot 'viewport.cpp'
$dll = Join-Path $output 'xtmupdfviewport.dll'
$obj = Join-Path $output 'viewport.obj'
$lib = Join-Path $sdk 'lib\mupdfcpp64.lib'
$command = 'call "' + $vcvars + '" >nul && cl /nologo /O2 /EHsc /MD /LD /std:c++17 /I"' + (Join-Path $sdk 'include') + '" "' + $cpp + '" /Fo"' + $obj + '" "' + $lib + '" /link /OUT:"' + $dll + '" /IMPLIB:"' + (Join-Path $output 'viewport.lib') + '"'
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw 'Native viewport build failed.' }
Copy-Item -LiteralPath (Join-Path $repo 'Tests\bin\mupdf-bench-deps\pymupdf\mupdfcpp64.dll') -Destination $output
