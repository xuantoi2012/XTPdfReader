param(
    [ValidateRange(1, 4)][int]$Rounds = 3,
    [string]$Cohort = 'reader-retention-2026-10-02',
    [string]$SourceManifest
)
$ErrorActionPreference = 'Stop'
$testDll = Join-Path $PSScriptRoot 'bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.PerformanceTests.dll'
$results = Join-Path (Split-Path -Parent $testDll) 'results'
if (!$SourceManifest) { $SourceManifest = Join-Path $results 'reader-validation-2026-10-02-sources.json' }
$SourceManifest = (Resolve-Path -LiteralPath $SourceManifest).Path
$runs = [System.Collections.Generic.List[object]]::new()
$previous = [Environment]::GetEnvironmentVariable('XTPDF_HOT_DOCUMENT_COPIES', 'Process')
$previousPool = [Environment]::GetEnvironmentVariable('XTPDF_PDFIUM_INSTANCES', 'Process')
try {
    $env:XTPDF_PDFIUM_INSTANCES = '2'
    for ($round = 1; $round -le $Rounds; $round++) {
        $variants = @('baseline', 'preview', 'balanced')
        if ($round % 2 -eq 0) { [array]::Reverse($variants) }
        elseif ($round -gt 1) { $variants = @('preview', 'balanced', 'baseline') }
        foreach ($variant in $variants) {
            $label = "$Cohort-r$round-$variant"
            $preview = $(if ($variant -eq 'baseline') { 0 } else { 16 })
            $env:XTPDF_HOT_DOCUMENT_COPIES = $(if ($variant -eq 'balanced') { '1' } else { '0' })
            & dotnet $testDll --retention-profile $label $preview --profile-sources $SourceManifest
            if ($LASTEXITCODE -ne 0) { throw "Failed profile: $label" }
            $r = Get-Content -Raw -LiteralPath (Join-Path $results "retention-$label.json") | ConvertFrom-Json
            if (!$r.AllReady -or !$r.SourcesUnchanged -or !$r.FullImageEvicted -or $r.PdfiumInstances -ne 2) { throw "Invalid profile: $label" }
            if ($preview -gt 0 -and (!$r.DisplayFallbackAvailable -or !$r.ImmediateStateFallback)) { throw "Missing display fallback: $label" }
            if ($variant -eq 'balanced' -and $r.DocumentsAfterIdle -ne 1) { throw "Hot replica was not consolidated: $label" }
            $r | Add-Member -NotePropertyName Variant -NotePropertyValue $variant
            $runs.Add($r)
        }
    }
    $reference = $runs[0]
    foreach ($run in $runs) {
        if ($run.Steps.Count -ne $reference.Steps.Count) { throw 'Workload length mismatch' }
        for ($i = 0; $i -lt $reference.Steps.Count; $i++) {
            $a = $reference.Steps[$i]; $b = $run.Steps[$i]
            if ($a.Stage -ne $b.Stage -or $a.Page -ne $b.Page -or $a.Zoom -ne $b.Zoom -or
                !$b.PixelHash -or $a.PixelHash -ne $b.PixelHash -or $a.PixelWidth -ne $b.PixelWidth -or $a.PixelHeight -ne $b.PixelHeight) {
                throw "Sharp pixels or workload mismatch: $($run.Label) step $i"
            }
        }
    }
    function Median($values) {
        $a = @($values | Sort-Object); $n = $a.Count
        if ($n % 2) { return [double]$a[[int][math]::Floor($n / 2)] }
        return ([double]$a[$n / 2 - 1] + [double]$a[$n / 2]) / 2
    }
    $summary = foreach ($variant in 'baseline', 'preview', 'balanced') {
        $g = @($runs | Where-Object Variant -eq $variant)
        [pscustomobject]@{
            Variant = $variant; Runs = $g.Count
            PeakMiB = Median $g.PeakPrivateMiB; BeforeIdleMiB = Median $g.BeforeIdlePrivateMiB; IdleMiB = Median $g.IdlePrivateMiB
            CpuSeconds = Median $g.CpuSeconds; DispatcherP95Ms = Median $g.DispatcherDelayP95Ms
            ColdReadinessSumMs = Median @($g | ForEach-Object { ($_.Steps | Where-Object Stage -eq 'cold-page' | Measure-Object ReadyMs -Sum).Sum })
            RevisitSharpMs = Median @($g | ForEach-Object Steps | Where-Object Stage -eq 'evicted-revisit' | Select-Object -ExpandProperty ReadyMs)
            ResumeSharperMs = Median @($g | ForEach-Object Steps | Where-Object Stage -eq 'resume-sharper' | Select-Object -ExpandProperty ReadyMs)
            ResumeDeepMs = Median @($g | ForEach-Object Steps | Where-Object Stage -eq 'resume-deep' | Select-Object -ExpandProperty ReadyMs)
            ResumeNormalMs = Median @($g | ForEach-Object Steps | Where-Object Stage -eq 'resume-normal' | Select-Object -ExpandProperty ReadyMs)
            DocumentsAfterIdle = Median $g.DocumentsAfterIdle
            ImmediateFallbackRuns = @($g | Where-Object ImmediateStateFallback).Count
            SharpPixelsMatch = $true; SourcesUnchanged = $true
        }
    }
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results "$Cohort-summary.json") -Encoding utf8
    $summary | Format-Table Variant,PeakMiB,IdleMiB,ColdReadinessSumMs,ResumeSharperMs,ResumeDeepMs,ImmediateFallbackRuns -AutoSize
}
finally {
    [Environment]::SetEnvironmentVariable('XTPDF_HOT_DOCUMENT_COPIES', $previous, 'Process')
    [Environment]::SetEnvironmentVariable('XTPDF_PDFIUM_INSTANCES', $previousPool, 'Process')
}
