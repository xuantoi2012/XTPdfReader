param(
    [Parameter(Mandatory)] [string]$FirstPdf,
    [Parameter(Mandatory)] [string]$SecondPdf,
    [int]$PageNumber = 31,
    [ValidateRange(1, 10)] [int]$Repeats = 3,
    [ValidateSet('reference', 'reference-background-gc', 'cache-only', 'cache-gc', 'cache-background-gc', 'park-spare-gc', 'park-spare-background-gc', 'park-all-gc', 'cache-gc-heap')]
    [string[]]$Modes = @('reference', 'reference-background-gc', 'park-spare-background-gc', 'park-all-gc'),
    [switch]$SinglePage,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$firstInput = (Resolve-Path -LiteralPath $FirstPdf).Path
$secondInput = (Resolve-Path -LiteralPath $SecondPdf).Path
$testProject = Join-Path $PSScriptRoot 'PerformanceTests.csproj'
if (!$SkipBuild) {
    & dotnet build $testProject -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
}
$dll = Join-Path $PSScriptRoot 'bin\Release\net10.0-windows10.0.19041.0\XTPdfMergeApp.PerformanceTests.dll'
$results = Join-Path (Split-Path -Parent $dll) 'results'
$started = [DateTime]::UtcNow
# Fresh processes, sequential workload, rotated order. Parallel runs distort memory and latency comparisons.
for ($repeat = 0; $repeat -lt $Repeats; $repeat++) {
    for ($index = 0; $index -lt $Modes.Count; $index++) {
        $trialMode = $Modes[($index + $repeat) % $Modes.Count]
        $trialArgs = @($dll, '--reclaim-memory-profile', $firstInput, $secondInput, $trialMode, "$PageNumber")
        if (!$SinglePage) { $trialArgs += 'neighbors' }
        & dotnet @trialArgs
        if ($LASTEXITCODE -ne 0) { throw "Reclamation experiment failed: $trialMode, repeat $($repeat + 1)." }
    }
}
$samples = @(Get-ChildItem -LiteralPath $results -Filter "reclaim-*-page-$PageNumber-*.json" |
    Where-Object LastWriteTimeUtc -ge $started | ForEach-Object {
        $rows = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        $baseline = ($rows | Where-Object Phase -eq 'adaptive-reference').Data
        $reclaimed = $rows | Where-Object Phase -eq 'reclaimed'
        $zoom = ($rows | Where-Object Phase -eq 'deep-zoom-pan').Data
        $neighbor = ($rows | Where-Object Phase -eq 'neighbor-deep-zoom').Data
        [pscustomobject]@{
            File = $_.Name; Mode = $reclaimed.Mode; Neighbors = !$SinglePage
            BeforePrivateMiB = $baseline.CombinedPrivateMiB
            AfterPrivateMiB = $reclaimed.Data.CombinedPrivateMiB
            SavedPrivateMiB = $baseline.CombinedPrivateMiB - $reclaimed.Data.CombinedPrivateMiB
            ReclaimMs = $reclaimed.Data.Action.ReclaimMs
            DispatcherGapMs = $reclaimed.Data.Action.MaxDispatcherGapMs
            DeepZoomMs = $zoom.SharpCropMs
            NeighborDeepZoomMs = $neighbor.SharpCropMs
        }
    })
$summaryPath = Join-Path $results "reclaim-batch-$($started.ToString('yyyyMMdd-HHmmss')).json"
$samples | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding utf8
$samples | Format-Table Mode, BeforePrivateMiB, AfterPrivateMiB, SavedPrivateMiB, DeepZoomMs, NeighborDeepZoomMs -AutoSize
Write-Output "Raw batch summary: $summaryPath"
