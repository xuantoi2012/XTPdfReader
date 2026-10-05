param(
    [string]$Exe = 'C:\Users\Admin\source\repos\tmp\pfgpu\pdfium\out\GpuPoc\xt_gpu_poc.exe',
    [string]$Manifest = "$PSScriptRoot\..\bin\Release\net10.0-windows10.0.19041.0\results\reader-validation-2026-10-02-sources.json",
    [string]$Output = "$PSScriptRoot\..\bin\gpu-poc"
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$files = Get-Content -LiteralPath $Manifest | ConvertFrom-Json
$runs = @()
foreach ($index in 2, 3) {
    $path = $files[$index].Path
    $before = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    foreach ($page in 11, 30) {
        foreach ($repeat in 1..3) {
            $prefix = Join-Path $Output "file$index-page$page-run$repeat"
            $lines = & $Exe $path $page $prefix 30
            if ($LASTEXITCODE) { throw "Benchmark failed: file$index page$page repeat$repeat" }
            $lines | Set-Content -LiteralPath "$prefix.csv"
            $metrics = [ordered]@{}
            foreach ($line in $lines) {
                $parts = $line -split ',', 2
                if ($parts.Count -ne 2) { throw "Unexpected output: $line" }
                if ($parts[0] -eq 'renderer') { $metrics[$parts[0]] = $parts[1] }
                else { $metrics[$parts[0]] = [double]::Parse($parts[1], [Globalization.CultureInfo]::InvariantCulture) }
            }
            $runs += [pscustomobject]@{ File = "file$index"; Page = $page; Repeat = $repeat; Metrics = $metrics }
            Write-Host "file$index page$page run$repeat CPU=$($metrics.direct_cpu_median_ms) GPU=$($metrics.picture_gpu_median_ms) ms"
        }
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $before) { throw 'Input PDF changed during benchmark' }
}
$runs | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output 'runs.json')
