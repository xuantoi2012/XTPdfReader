param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][string]$StopPath,
    [int]$DurationSeconds = 900,
    [int]$SampleMilliseconds = 100
)

$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class DesktopProcessCounters {
    [StructLayout(LayoutKind.Sequential)]
    public struct Io {
        public ulong ReadOperations, WriteOperations, OtherOperations;
        public ulong ReadBytes, WriteBytes, OtherBytes;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetProcessIoCounters(IntPtr process, out Io counters);
}
'@

$output = [IO.Path]::GetFullPath($OutputPath)
$stop = [IO.Path]::GetFullPath($StopPath)
if (Test-Path -LiteralPath $stop) { throw "Stop file already exists: $stop" }
$writer = [IO.StreamWriter]::new($output, $false, [Text.UTF8Encoding]::new($false))
$writer.AutoFlush = $true
$writer.WriteLine('Utc,ElapsedSeconds,Process,Pid,CpuSeconds,PrivateBytes,WorkingSetBytes,Threads,Handles,ReadOperations,ReadBytes,WriteOperations,WriteBytes,OtherBytes')
$watch = [Diagnostics.Stopwatch]::StartNew()
Write-Output "COLLECTOR PID=$PID OUTPUT=$output"
try {
    while ($watch.Elapsed.TotalSeconds -lt $DurationSeconds -and -not (Test-Path -LiteralPath $stop)) {
        foreach ($name in @('FoxitPhantomPDF', 'XTPdfMergeApp')) {
            foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
                try {
                    $process.Refresh()
                    $io = [DesktopProcessCounters+Io]::new()
                    $valid = [DesktopProcessCounters]::GetProcessIoCounters($process.Handle, [ref]$io)
                    $writer.WriteLine(([string]::Join(',', @(
                        [DateTime]::UtcNow.ToString('o'), $watch.Elapsed.TotalSeconds.ToString('F6', [Globalization.CultureInfo]::InvariantCulture),
                        $name, $process.Id, $process.TotalProcessorTime.TotalSeconds.ToString('F6', [Globalization.CultureInfo]::InvariantCulture),
                        $process.PrivateMemorySize64, $process.WorkingSet64, $process.Threads.Count, $process.HandleCount,
                        $(if ($valid) { $io.ReadOperations } else { '' }), $(if ($valid) { $io.ReadBytes } else { '' }),
                        $(if ($valid) { $io.WriteOperations } else { '' }), $(if ($valid) { $io.WriteBytes } else { '' }),
                        $(if ($valid) { $io.OtherBytes } else { '' })
                    ))))
                } catch [InvalidOperationException] { }
                finally { $process.Dispose() }
            }
        }
        Start-Sleep -Milliseconds $SampleMilliseconds
    }
} finally { $writer.Dispose() }
Write-Output "FINISHED after $($watch.Elapsed.TotalSeconds.ToString('F1')) seconds"
