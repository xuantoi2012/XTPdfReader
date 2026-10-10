# Builds xtpdfworker.exe against a MuPDF source tree that carries the XT patch
# (mupdf-xt/mupdf-1.28.2-xt.patch), linked statically: no mupdfcpp64.dll and no PyMuPDF.
#
#   -MuPdfSource  extracted mupdf-1.28.2-source (default: env XTPDF_MUPDF_SRC, else ..\..\..\mupdf-src\mupdf-1.28.2-source)
#
# No profile guided optimisation: a PGO build was measured 5-15 % faster but corrupted the heap when a
# render was cancelled (MuPDF unwinds with setjmp/longjmp, which PGO optimises unsafely).
#
#   -Out          output folder (default Native\XtMuPdfWorker\bin\static)
#
# The patch keeps every render byte identical to the unpatched engine. It adds banded multi-threaded
# rendering, atomic reference counts and a few lock-free fast paths. See mupdf-xt/README.md.
param(
    [string]$MuPdfSource = $env:XTPDF_MUPDF_SRC,
    [string]$MimallocSource = $env:XTPDF_MIMALLOC_SRC,
    [string]$Out,
    [switch]$SkipLibraries
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = (Resolve-Path (Join-Path $here '..\..')).Path
if (-not $MuPdfSource) { $MuPdfSource = Join-Path $repo '..\..\mupdf-src\mupdf-1.28.2-source' }
$MuPdfSource = (Resolve-Path $MuPdfSource).Path
if (-not $Out) { $Out = Join-Path $here 'bin\static' }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path

foreach ($required in 'include\mupdf\fitz.h', 'platform\win32\mupdf.sln', 'thirdparty\tesseract') {
    if (-not (Test-Path -LiteralPath (Join-Path $MuPdfSource $required))) {
        throw "Not a MuPDF 1.28.2 source tree: missing $required (download mupdf-1.28.2-source.tar.gz and extract it)."
    }
}

# 1. Apply the patch once; a marker records that the tree is patched.
$marker = Join-Path $MuPdfSource '.xt-patched'
if (-not (Test-Path -LiteralPath $marker)) {
    $patch = Join-Path $here 'mupdf-xt\mupdf-1.28.2-xt.patch'
    $git = (Get-Command git -ErrorAction SilentlyContinue)
    if (-not $git) { throw 'git is needed to apply the MuPDF patch.' }
    & git -C $MuPdfSource apply --whitespace=nowarn --unsafe-paths $patch
    if ($LASTEXITCODE -ne 0) { throw 'Applying the MuPDF patch failed (is the tree already modified?).' }
    Set-Content -LiteralPath $marker -Value 'mupdf-1.28.2-xt.patch'
}

# mimalloc (MIT, https://github.com/microsoft/mimalloc, tested with v2.1.7): optional allocator, built into the exe.
if (-not $MimallocSource) { $MimallocSource = Join-Path $repo '..\..\mimalloc-src\mimalloc-2.1.7' }
$mimalloc = Test-Path -LiteralPath (Join-Path $MimallocSource 'src\static.c')
if ($mimalloc) { $MimallocSource = (Resolve-Path $MimallocSource).Path } else { Write-Warning "mimalloc not found at ${MimallocSource} - using the CRT heap." }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ x64 tools were not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'

# 2. The MuPDF libraries (Release, x64, AVX2). Retargeted to the installed toolset.
$libDir = Join-Path $MuPdfSource 'platform\win32\x64\Release'
if (-not $SkipLibraries) {
    $installedToolset = (Get-ChildItem (Join-Path $vs 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
    $major = [int]($installedToolset.Split('.')[1])          # 14.51 -> 51
    $toolsetName = if ($major -ge 50) { 'v145' } elseif ($major -ge 40) { 'v143' } else { 'v142' }
    $env:_CL_ = '/arch:AVX2'
    try {
        & $msbuild (Join-Path $MuPdfSource 'platform\win32\mupdf.sln') -t:libmupdf -p:Configuration=Release -p:Platform=x64 `
            "-p:PlatformToolset=$toolsetName" -m -v:m -nologo
        if ($LASTEXITCODE -ne 0) { throw 'Building the MuPDF libraries failed.' }
    } finally { Remove-Item Env:\_CL_ -ErrorAction SilentlyContinue }
}
$libs = 'libmupdf', 'libresources', 'libthirdparty', 'libextract', 'libharfbuzz', 'libleptonica', 'libmubarcode', 'libpkcs7', 'libtesseract', 'libzxing' |
    ForEach-Object { '"' + (Join-Path $libDir "$_.lib") + '"' }
$system = 'advapi32.lib user32.lib gdi32.lib comdlg32.lib shell32.lib crypt32.lib ws2_32.lib'

function Invoke-Cl([string]$exe, [string]$linkOptions) {
    $bat = Join-Path $Out 'build-worker.bat'
    $extraDefines = ''; $extraSources = ''
    if ($mimalloc) {
        $extraDefines = '/DXT_MIMALLOC /DMI_STATIC_LIB /DNDEBUG /I"' + (Join-Path $MimallocSource 'include') + '" '
        $extraSources = ' "' + (Join-Path $MimallocSource 'src\static.c') + '"'
    }
    $cl = 'cl /nologo /O2 /EHsc /MD /GL /std:c++17 /W3 /arch:AVX2 /DUNICODE /D_UNICODE /DXT_PATCHED_MUPDF ' + $extraDefines +
        '/I"' + (Join-Path $MuPdfSource 'include') + '" "' + (Join-Path $here 'worker.cpp') + '"' + $extraSources + ' ' + ($libs -join ' ') + ' ' + $system +
        ' /Fo"' + $Out + '\\" /link /LTCG ' + $linkOptions + ' /OUT:"' + $exe + '"'
    Set-Content -LiteralPath $bat -Encoding ascii -Value @('@echo off', "call `"$vcvars`" >nul", $cl)
    & $env:ComSpec /d /c $bat
    if ($LASTEXITCODE -ne 0) { throw 'Building the worker failed.' }
}

$exe = Join-Path $Out 'xtpdfworker.exe'
Invoke-Cl $exe ''
"built: $exe"
