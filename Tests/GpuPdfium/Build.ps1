param(
    [string]$PdfiumRoot = 'C:\Users\Admin\source\repos\tmp\pfgpu\pdfium',
    [string]$AngleRoot = 'C:\Users\Admin\source\repos\tmp\pfgpu\angle-runtime\runtimes\win-x64\native',
    [string]$VisualStudio = 'C:\Program Files\Microsoft Visual Studio\18\Community',
    [string]$PythonBin = 'C:\Users\Admin\AppData\Local\vpython-root.0\store\uv_venv-ms9pm46gcoi2j4hu0dp5c5cfhc\contents\Scripts'
)
$ErrorActionPreference = 'Stop'
$revision = git -C $PdfiumRoot rev-parse HEAD
if ($LASTEXITCODE -or $revision -ne '7cfa57a91284ba907cb0facaf9cd8e4a570fe5c7') { throw 'Unexpected PDFium source revision' }
$patch = Join-Path $PSScriptRoot 'pdfium-patches\0001-skia-picture-api.patch'
if (!(Select-String -LiteralPath "$PdfiumRoot\public\fpdfview.h" -SimpleMatch 'FPDF_CreateSkiaPicture' -Quiet)) {
    git -C $PdfiumRoot apply --check $patch
    if ($LASTEXITCODE) { throw 'The PDFium fork patch no longer applies cleanly' }
    git -C $PdfiumRoot apply $patch
    if ($LASTEXITCODE) { throw 'Applying the PDFium fork patch failed' }
}
if (!(Select-String -LiteralPath "$PdfiumRoot\BUILD.gn" -SimpleMatch '//xt_gpu_poc:xt_gpu_poc' -Quiet)) {
    throw 'Add //xt_gpu_poc:xt_gpu_poc to the external PDFium default group first; see README.md'
}
$env:DEPOT_TOOLS_WIN_TOOLCHAIN = '0'
$env:DEPOT_TOOLS_UPDATE = '0'
$env:GYP_MSVS_OVERRIDE_PATH = $VisualStudio
$env:PATH = "$PythonBin;$([IO.Path]::GetDirectoryName($PdfiumRoot))\depot_tools;$env:PATH"
New-Item -ItemType Directory -Path "$PdfiumRoot\xt_gpu_poc" -Force | Out-Null
Copy-Item "$PSScriptRoot\main.cpp", "$PSScriptRoot\BUILD.gn" -Destination "$PdfiumRoot\xt_gpu_poc" -Force
New-Item -ItemType Directory -Path "$PdfiumRoot\out\GpuPoc" -Force | Out-Null
Copy-Item "$PSScriptRoot\args.gn" -Destination "$PdfiumRoot\out\GpuPoc\args.gn" -Force
Push-Location $PdfiumRoot
try {
    & .\buildtools\win\gn.exe gen out/GpuPoc
    if ($LASTEXITCODE) { throw 'GN generation failed' }
    $ninja = "$VisualStudio\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"
    & $ninja -C out/GpuPoc xt_gpu_poc -j4
    if ($LASTEXITCODE) { throw 'Native build failed' }
    Copy-Item "$AngleRoot\libEGL.dll", "$AngleRoot\libGLESv2.dll" -Destination .\out\GpuPoc -Force
    Write-Output "$PdfiumRoot\out\GpuPoc\xt_gpu_poc.exe"
} finally { Pop-Location }
