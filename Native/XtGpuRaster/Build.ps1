$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'Visual Studio C++ x64 tools were not found.' }
$vcvars = Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
$command = 'call "' + $vcvars + '" >nul 2>&1 && cl /nologo /O2 /EHsc /MD /std:c++17 /W4 /LD "' + (Join-Path $PSScriptRoot 'renderer.cpp') + '" /Fo"' + $out + '\renderer.obj" d3d9.lib user32.lib /link /OUT:"' + $out + '\xtgpuraster.dll" /IMPLIB:"' + $out + '\xtgpuraster.lib"'
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE -ne 0) { throw 'GPU renderer build failed.' }
