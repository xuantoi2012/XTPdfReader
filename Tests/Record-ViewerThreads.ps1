param([Parameter(Mandatory=$true)][string]$OutputPath,[Parameter(Mandatory=$true)][string]$StopPath,[int]$DurationSeconds=900)
# Passive only: no input injection, process suspension, or window control.
$ErrorActionPreference='Stop'
$writer=[IO.StreamWriter]::new([IO.Path]::GetFullPath($OutputPath),$false)
$writer.AutoFlush=$true
$writer.WriteLine('Utc,Process,Pid,Tid,CpuMilliseconds')
$watch=[Diagnostics.Stopwatch]::StartNew()
try {
 while($watch.Elapsed.TotalSeconds -lt $DurationSeconds -and -not(Test-Path -LiteralPath $StopPath)) {
  foreach($name in @('FoxitPhantomPDF','XTPdfMergeApp')) {
   foreach($p in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
    try {
     $utc=[DateTime]::UtcNow.ToString('o')
     foreach($t in $p.Threads) {
      try { $writer.WriteLine("$utc,$name,$($p.Id),$($t.Id),$($t.TotalProcessorTime.TotalMilliseconds.ToString('F3',[Globalization.CultureInfo]::InvariantCulture))") } catch {}
     }
    } finally { $p.Dispose() }
   }
  }
  Start-Sleep -Milliseconds 100
 }
} finally { $writer.Dispose() }
