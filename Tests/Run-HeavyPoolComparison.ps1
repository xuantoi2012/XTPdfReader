param([int]$Rounds = 3, [string]$Cohort = 'pool-fixed-2026-10-02')
$ErrorActionPreference = 'Stop'
if ($Rounds -lt 1 -or $Rounds -gt 6) { throw 'Rounds must be between 1 and 6.' }
$testDll = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.PerformanceTests.dll'
$results = Join-Path (Split-Path -Parent $testDll) 'results'
New-Item -ItemType Directory -Force -Path $results | Out-Null
$manifest = Join-Path $results ($Cohort + '-sources.json')
$recent = Get-Content -LiteralPath (Join-Path $env:LOCALAPPDATA 'XTPdfReader/recent.json') -Raw | ConvertFrom-Json
$sources = @($recent | Sort-Object OpenedUtc -Descending | Where-Object { $_.Size -ge 100MB -and (Test-Path -LiteralPath $_.Path) } | Select-Object -First 2)
if ($sources.Count -ne 2) { throw 'Two accessible recent PDFs larger than 100 MiB are required.' }
$sources | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifest -Encoding utf8
$previous = $env:XTPDF_PDFIUM_INSTANCES
$runs = [System.Collections.Generic.List[object]]::new()

function Run-Profile([int]$Count, [string]$Label) {
    $env:XTPDF_PDFIUM_INSTANCES = [string]$Count
    Write-Output "BEGIN $Label ($Count PDFium instances)"
    & dotnet $testDll --background-heavy-profile $Label 4 --profile-sources $manifest
    if ($LASTEXITCODE -ne 0) { throw "Profile failed: $Label" }
    $result = Get-Content -LiteralPath (Join-Path $results ('heavy-background-' + $Label + '.json')) -Raw | ConvertFrom-Json
    if ($result.PdfiumInstances -ne $Count -or !$result.AllReady -or !$result.SourcesUnchanged -or !$result.BuffersReady) {
        throw "Invalid measurement: $Label"
    }
    if (($result.Measurements | Where-Object ScrollTicks | Where-Object { $_.ScrollTicks -ne 100 -or $_.ScrollDistanceDip -ne 6500 }).Count -ne 0) {
        throw "Scroll workload changed: $Label"
    }
    $runs.Add($result)
}

function Median($Values) {
    $a = @($Values | Sort-Object)
    $n = $a.Count
    if ($n % 2) { return [double]$a[[int][math]::Floor($n / 2)] }
    return ([double]$a[$n / 2 - 1] + [double]$a[$n / 2]) / 2
}

try {
    Run-Profile 4 ($Cohort + '-warmup')
    $runs.Clear()
    $orders = @(@(2, 4, 6, 3), @(6, 3, 2, 4), @(3, 6, 4, 2))
    for ($round = 0; $round -lt $Rounds; $round++) {
        foreach ($count in $orders[$round % $orders.Count]) {
            Run-Profile $count ($Cohort + '-r' + ($round + 1) + '-k' + $count)
        }
    }
    foreach ($file in 1, 2) {
        $hashes = @($runs | ForEach-Object Measurements | Where-Object { $_.File -eq $file -and $_.PixelHash } | Select-Object -ExpandProperty PixelHash -Unique)
        if ($hashes.Count -ne 1) { throw "Pixel mismatch across configurations for file $file" }
    }
    $summary = foreach ($count in 2, 3, 4, 6) {
        $group = @($runs | Where-Object PdfiumInstances -eq $count)
        $zoom = @($group | ForEach-Object Measurements | Where-Object ZoomReady)
        $scroll = @($group | ForEach-Object Measurements | Where-Object ScrollTicks)
        [pscustomobject]@{
            Instances = $count
            Runs = $group.Count
            ScenarioMedianSeconds = [math]::Round((Median $group.ScenarioSeconds), 3)
            ScenarioMinSeconds = ($group.ScenarioSeconds | Measure-Object -Minimum).Minimum
            ScenarioMaxSeconds = ($group.ScenarioSeconds | Measure-Object -Maximum).Maximum
            ReadinessMedianMs = [math]::Round((Median $group.TotalReadinessMs), 1)
            PeakMedianMB = [math]::Round((Median $group.PeakPrivateMB), 1)
            PeakMaxMB = ($group.PeakPrivateMB | Measure-Object -Maximum).Maximum
            IdleMedianMB = [math]::Round((Median $group.AfterIdlePrivateMB), 1)
            CpuMedianSeconds = [math]::Round((Median $group.CpuSeconds), 2)
            HeavyZoomMedianMs = [math]::Round((Median ($zoom | Where-Object File -eq 2).ZoomMs), 1)
            ResumeMedianMs = [math]::Round((Median $group.ResumeMs), 1)
            ActiveDispatcherP95MedianMs = [math]::Round((Median $group.DispatcherDelayP95Ms), 1)
            MissingScrollTicksMedian = Median ($scroll | Where-Object File -eq 2).NotReadyTicks
            AllSourcesUnchanged = @($group | Where-Object { !$_.SourcesUnchanged }).Count -eq 0
            AllPagesReady = @($group | Where-Object { !$_.AllReady }).Count -eq 0
            PixelHashesMatch = $true
        }
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $results ($Cohort + '-summary.json')) -Encoding utf8
    $summary | Format-Table Instances,Runs,ScenarioMedianSeconds,PeakMedianMB,IdleMedianMB,HeavyZoomMedianMs,ResumeMedianMs -AutoSize
}
finally {
    [Environment]::SetEnvironmentVariable('XTPDF_PDFIUM_INSTANCES', $previous, 'Process')
}
