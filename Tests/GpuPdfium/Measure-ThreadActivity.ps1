param(
    [string]$ProcessName = 'FoxitPhantomPDF',
    [int]$Seconds = 45,
    [string]$OutputPath = "$PSScriptRoot\..\bin\foxit-study\threads.csv"
)
$ErrorActionPreference = 'Stop'
$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$previous = @{}
$rows = [System.Collections.Generic.List[object]]::new()
$watch = [System.Diagnostics.Stopwatch]::StartNew()
while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
    $process = Get-Process -Name $ProcessName | Select-Object -First 1
    $elapsed = $watch.Elapsed.TotalMilliseconds
    foreach ($thread in $process.Threads) {
        try {
            $cpu = $thread.TotalProcessorTime.TotalMilliseconds
            $key = "$($process.Id):$($thread.Id)"
            $delta = if ($previous.ContainsKey($key)) { $cpu - $previous[$key] } else { 0 }
            $previous[$key] = $cpu
            $rows.Add([pscustomobject]@{
                Utc = [DateTime]::UtcNow.ToString('o')
                ElapsedMs = [math]::Round($elapsed, 2)
                ProcessId = $process.Id
                ThreadId = $thread.Id
                CpuDeltaMs = $delta
                State = $thread.ThreadState.ToString()
                PrivateBytes = $process.PrivateMemorySize64
            })
        } catch { }
    }
    Start-Sleep -Milliseconds 100
}
$rows | Export-Csv -LiteralPath $OutputPath -NoTypeInformation
$rows | Group-Object ThreadId | ForEach-Object {
    [pscustomobject]@{ ThreadId = $_.Name; CpuMs = ($_.Group | Measure-Object CpuDeltaMs -Sum).Sum }
} | Sort-Object CpuMs -Descending | Select-Object -First 12 | Format-Table
Write-Output "Raw samples: $OutputPath"
