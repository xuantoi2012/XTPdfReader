param(
    [Parameter(Mandatory = $true)][string]$CsvPath,
    [Parameter(Mandatory = $true)][string]$WindowsPath
)

$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
function Convert-UtcTime($value) {
    if ($value -is [DateTimeOffset]) { return $value }
    if ($value -is [DateTime]) { return [DateTimeOffset]::new($value) }
    return [DateTimeOffset]::Parse($value, $culture)
}
$rows = @(Import-Csv -LiteralPath $CsvPath | ForEach-Object {
    [pscustomobject]@{
        Utc = [DateTimeOffset]::Parse($_.Utc, $culture)
        Pid = [int]$_.Pid
        Cpu = [double]::Parse($_.CpuSeconds, $culture)
        PrivateMiB = [double]::Parse($_.PrivateBytes, $culture) / 1MB
        WorkingMiB = [double]::Parse($_.WorkingSetBytes, $culture) / 1MB
        ReadBytes = [double]::Parse($_.ReadBytes, $culture)
        WriteBytes = [double]::Parse($_.WriteBytes, $culture)
    }
})
$windows = Get-Content -LiteralPath $WindowsPath -Raw | ConvertFrom-Json
$results = foreach ($window in $windows) {
    $start = Convert-UtcTime $window.StartUtc
    $end = Convert-UtcTime $window.EndUtc
    if ($end -le $start) { throw "Invalid window: $($window.Stage)" }
    $samples = @($rows | Where-Object { $_.Pid -eq $window.Pid -and $_.Utc -ge $start -and $_.Utc -le $end } | Sort-Object Utc)
    if ($samples.Count -lt 2) { throw "Not enough samples: $($window.Stage)" }
    $first = $samples[0]
    $last = $samples[-1]
    $seconds = ($last.Utc - $first.Utc).TotalSeconds
    $private = $samples.PrivateMiB | Measure-Object -Minimum -Maximum -Average
    $working = $samples.WorkingMiB | Measure-Object -Average -Maximum
    [pscustomobject]@{
        App = $window.App
        Stage = $window.Stage
        Pid = $window.Pid
        StartUtc = $first.Utc.ToString('o')
        EndUtc = $last.Utc.ToString('o')
        Samples = $samples.Count
        PrivateMeanMiB = [Math]::Round($private.Average, 2)
        PrivateMinMiB = [Math]::Round($private.Minimum, 2)
        PrivateMaxMiB = [Math]::Round($private.Maximum, 2)
        WorkingMeanMiB = [Math]::Round($working.Average, 2)
        WorkingMaxMiB = [Math]::Round($working.Maximum, 2)
        CpuSeconds = [Math]::Round($last.Cpu - $first.Cpu, 4)
        CpuCoreEquivalent = [Math]::Round(($last.Cpu - $first.Cpu) / $seconds, 4)
        GenericIoReadMiB = [Math]::Round(($last.ReadBytes - $first.ReadBytes) / 1MB, 3)
        GenericIoWriteMiB = [Math]::Round(($last.WriteBytes - $first.WriteBytes) / 1MB, 3)
    }
}
$results | ConvertTo-Json -Depth 4
