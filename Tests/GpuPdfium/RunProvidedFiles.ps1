param(
    [Parameter(Mandatory)] [string]$BinhDo,
    [Parameter(Mandatory)] [string]$ThoatNuocMua,
    [string]$Exe = 'C:\Users\Admin\source\repos\tmp\pfgpu\pdfium\out\GpuPoc\xt_gpu_poc.exe',
    [string]$Output = "$PSScriptRoot\..\bin\gpu-poc\provided-files"
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $Output -Force | Out-Null
$cases = @(
    @{ Name = 'bindo-page-3'; Path = $BinhDo; Page = 3 },
    @{ Name = 'tnm-page-1'; Path = $ThoatNuocMua; Page = 1 },
    @{ Name = 'tnm-page-11'; Path = $ThoatNuocMua; Page = 11 },
    @{ Name = 'tnm-page-61'; Path = $ThoatNuocMua; Page = 61 }
)

$hashBefore = @{}
foreach ($path in $cases.Path | Select-Object -Unique) {
    if (!(Test-Path -LiteralPath $path)) { throw "Input PDF is unavailable: $path" }
    $hashBefore[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}

$results = foreach ($case in $cases) {
    $prefix = Join-Path $Output $case.Name
    $lines = & $Exe $case.Path $case.Page $prefix 5 64
    if ($LASTEXITCODE) { throw "Benchmark failed: $($case.Name)" }
    $lines | Set-Content -LiteralPath "$prefix.csv"
    $metrics = [ordered]@{}
    foreach ($line in $lines) {
        $parts = $line -split ',', 2
        if ($parts.Count -eq 2) { $metrics[$parts[0]] = $parts[1] }
    }
    [pscustomobject]@{ Name = $case.Name; Page = $case.Page; Metrics = $metrics }
}

foreach ($path in $hashBefore.Keys) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hashBefore[$path]) {
        throw "Input PDF changed during benchmark: $path"
    }
}

$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output 'results.json')
