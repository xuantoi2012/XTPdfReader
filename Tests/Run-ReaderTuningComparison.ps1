param(
    [ValidateRange(1, 4)][int]$Rounds = 2,
    [string]$Cohort = 'reader-tuning-2026-10-02',
    [string]$SourceManifest,
    [string[]]$Variants = @('baseline', 'prefetch2', 'cache32', 'cache96', 'native8', 'prefetch-hot', 'warm1'),
    [ValidateRange(0, 4)][int]$ZoomCycles = 0,
    [switch]$DeepZoom
)
$ErrorActionPreference = 'Stop'
$testDll = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.PerformanceTests.dll'
$results = Join-Path (Split-Path -Parent $testDll) 'results'
if (!$SourceManifest) { $SourceManifest = Join-Path $results 'pool-fixed-2026-10-02-sources.json' }
$SourceManifest = (Resolve-Path -LiteralPath $SourceManifest).Path
$sources = @(Get-Content -LiteralPath $SourceManifest -Raw | ConvertFrom-Json)
$plan = @{}
foreach ($name in @('baseline', 'prefetch2', 'cache32', 'cache96', 'native8', 'prefetch-hot', 'warm1', 'native8-warm1', 'prefetch2-warm1', 'prefetch-affinity', 'affinity', 'viewport-first')) {
    $plan[$name] = [pscustomobject]@{
        Name = $name; Prefetch = 4; ReaderCacheMiB = 64; NativeCachePages = 4
        WarmFiles = 2; KeepPrefetchedNativePages = $false; ZoomCycles = $ZoomCycles; DeepZoom = [bool]$DeepZoom
        CachedPagePenalty = 3
        PreferViewportRegions = $false
    }
}
$plan['prefetch2'].Prefetch = 2
$plan['cache32'].ReaderCacheMiB = 32
$plan['cache96'].ReaderCacheMiB = 96
$plan['native8'].NativeCachePages = 8
$plan['prefetch-hot'].KeepPrefetchedNativePages = $true
$plan['warm1'].WarmFiles = 1
$plan['native8-warm1'].NativeCachePages = 8
$plan['native8-warm1'].WarmFiles = 1
$plan['prefetch2-warm1'].Prefetch = 2
$plan['prefetch2-warm1'].WarmFiles = 1
$plan['prefetch-affinity'].KeepPrefetchedNativePages = $true
$plan['prefetch-affinity'].CachedPagePenalty = 9
$plan['affinity'].CachedPagePenalty = 9
$plan['viewport-first'].PreferViewportRegions = $true
foreach ($name in $Variants) { if (!$plan.ContainsKey($name)) { throw "Unknown variant: $name" } }
$runs = [System.Collections.Generic.List[object]]::new()
$previous = [Environment]::GetEnvironmentVariable('XTPDF_PDFIUM_INSTANCES', 'Process')

function Run-Variant([string]$Name, [string]$Label) {
    $config = $plan[$Name]
    $options = Join-Path $results ($Cohort + '-' + $Name + '-options.json')
    $config | ConvertTo-Json | Set-Content -LiteralPath $options -Encoding utf8
    Write-Output "BEGIN $Label"
    & dotnet $testDll --background-heavy-profile $Label $config.Prefetch --profile-sources $SourceManifest --profile-tuning $options
    if ($LASTEXITCODE -ne 0) { throw "Profile failed: $Label" }
    $r = Get-Content -LiteralPath (Join-Path $results ('heavy-background-' + $Label + '.json')) -Raw | ConvertFrom-Json
    if ($r.PdfiumInstances -ne 2 -or !$r.AllReady -or !$r.BuffersReady -or !$r.SourcesUnchanged) { throw "Invalid run: $Label" }
    $scroll = @($r.Measurements | Where-Object ScrollTicks)
    if ($scroll.Count -ne $sources.Count -or @($scroll | Where-Object { $_.ScrollTicks -ne 100 -or $_.ScrollDistanceDip -ne 6500 }).Count) {
        throw "Scroll workload mismatch: $Label"
    }
    $r | Add-Member -NotePropertyName Variant -NotePropertyValue $Name
    $runs.Add($r)
}

function Median($Values) {
    $a = @($Values | Sort-Object)
    $n = $a.Count
    if ($n % 2) { return [double]$a[[int][math]::Floor($n / 2)] }
    return ([double]$a[$n / 2 - 1] + [double]$a[$n / 2]) / 2
}

try {
    $env:XTPDF_PDFIUM_INSTANCES = '2'
    Run-Variant 'baseline' ($Cohort + '-warmup')
    $runs.Clear()
    for ($round = 0; $round -lt $Rounds; $round++) {
        $order = @($Variants)
        if ($round % 2) { [Array]::Reverse($order) }
        elseif ($round -gt 0 -and $order.Length -gt 2) { $order = @($order[2..($order.Length - 1)] + $order[0..1]) }
        foreach ($name in $order) { Run-Variant $name ($Cohort + '-r' + ($round + 1) + '-' + $name) }
    }
    foreach ($file in 1..$sources.Count) {
        $hashes = @($runs | ForEach-Object Measurements | Where-Object { $_.File -eq $file -and $_.PixelHash } | Select-Object -ExpandProperty PixelHash -Unique)
        if ($hashes.Count -ne 1) { throw "Pixel mismatch for file $file" }
        if ($DeepZoom) {
            foreach ($property in 'RegionHash', 'PanHash') {
                $hashes = @($runs | ForEach-Object Measurements | Where-Object { $_.File -eq $file -and $_.$property } | Select-Object -ExpandProperty $property -Unique)
                if ($hashes.Count -ne 1) { throw "$property mismatch for file $file" }
            }
        }
    }
    $summary = foreach ($name in $Variants) {
        $g = @($runs | Where-Object Variant -eq $name)
        $zoom = @($g | ForEach-Object Measurements | Where-Object ZoomReady)
        $scroll = @($g | ForEach-Object Measurements | Where-Object ScrollTicks)
        [pscustomobject]@{
            Variant = $name; Runs = $g.Count; Tuning = $g[0].Tuning; Prefetch = $g[0].Prefetch
            ScenarioSeconds = [math]::Round((Median $g.ScenarioSeconds), 3)
            ReadinessMs = [math]::Round((Median $g.TotalReadinessMs), 1)
            PeakMiB = Median $g.PeakPrivateMB; IdleMiB = Median $g.AfterIdlePrivateMB
            CpuSeconds = Median $g.CpuSeconds
            ZoomMs = [math]::Round((Median ($zoom | Where-Object File -eq $sources.Count).ZoomMs), 1)
            ResumeMs = Median $g.ResumeMs; ReturnMs = Median $g.ReturnMs
            MissingScrollTicks = Median ($scroll | Where-Object File -eq $sources.Count).NotReadyTicks
            DispatcherP95Ms = Median $g.DispatcherDelayP95Ms
            NativeParses = Median $g.ActiveParses.Parses
            RepeatedParses = Median $g.ActiveParses.RepeatedParses
            MultiReplicaPages = Median $g.ActiveParses.PagesParsedOnMultipleInstances
            CancelledRequests = Median $g.ActiveRequests.Cancelled
            NativeWaitMs = Median $g.ActiveDiagnostics.NativeWait.TotalMs
            BufferWaitMs = Median $g.ActiveDiagnostics.BufferWait.TotalMs
            RasterMs = Median $g.ActiveDiagnostics.Raster.TotalMs
            GcPauseMs = Median $g.ActiveDiagnostics.GcPauseMs
            PolicyTrims = Median $g.ActiveDiagnostics.PolicyTrims
            DeepZoomMs = $(if ($DeepZoom) { Median ($g | ForEach-Object Measurements | Where-Object { $_.File -eq $sources.Count -and $_.DeepReady }).DeepMs } else { $null })
            PanMs = $(if ($DeepZoom) { Median ($g | ForEach-Object Measurements | Where-Object { $_.File -eq $sources.Count -and $_.PanReady }).PanMs } else { $null })
            AllReady = $true; PixelHashesMatch = $true; SourcesUnchanged = $true
        }
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $results ($Cohort + '-summary.json')) -Encoding utf8
    $summary | Format-Table Variant,ReadinessMs,PeakMiB,IdleMiB,CpuSeconds,RepeatedParses,MissingScrollTicks -AutoSize
}
finally { [Environment]::SetEnvironmentVariable('XTPDF_PDFIUM_INSTANCES', $previous, 'Process') }
