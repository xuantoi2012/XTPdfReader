param([string]$Pdf, [string]$Out, [string]$Exe = 'C:\Program Files (x86)\Foxit Software\Foxit PhantomPDF\FoxitPhantomPDF.exe')
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class Mem {
  [StructLayout(LayoutKind.Sequential)] public struct IO { public ulong ro,wo,oo,rb,wb,ob; }
  [DllImport("kernel32.dll")] public static extern bool GetProcessIoCounters(IntPtr h, out IO c);
  [StructLayout(LayoutKind.Sequential)] public struct MBI { public ulong BaseAddress, AllocationBase; public uint AllocationProtect, pad1; public ulong RegionSize; public uint State, Protect, Type, pad2; }
  [DllImport("kernel32.dll")] public static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MBI mbi, int len);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  public static string Regions(IntPtr h) {
    ulong img=0,map=0,priv=0; int bigPriv=0; ulong bigPrivBytes=0; ulong a=0x10000;
    while (a < 0xFFFF0000UL) {
      MBI m; if (VirtualQueryEx(h,(IntPtr)(long)a,out m,Marshal.SizeOf(typeof(MBI)))==0) break;
      if (m.State==0x1000) { // COMMIT
        if (m.Type==0x1000000) img+=m.RegionSize; else if (m.Type==0x40000) map+=m.RegionSize; else if (m.Type==0x20000){ priv+=m.RegionSize; if(m.RegionSize>=(4UL<<20)){bigPriv++; bigPrivBytes+=m.RegionSize;} } }
      ulong n = m.BaseAddress + m.RegionSize; if (n<=a) break; a=n; }
    return string.Format("{0},{1},{2},{3},{4}", img>>20, map>>20, priv>>20, bigPriv, bigPrivBytes>>20);
  }
}
'@
Remove-Item $Out -ErrorAction SilentlyContinue
"t,phase,private_mb,ws_mb,threads,handles,read_mb,read_ops,write_mb,img_mb,mapped_mb,privcommit_mb,big_priv_regions,big_priv_mb" | Set-Content $Out
$sw = [Diagnostics.Stopwatch]::StartNew()
$global:p = $null
function Snap($phase) {
  $p = $global:p; $p.Refresh(); $io = New-Object Mem+IO; [void][Mem]::GetProcessIoCounters($p.Handle, [ref]$io)
  $r = [Mem]::Regions($p.Handle)
  ('{0:F1},{1},{2},{3},{4},{5},{6},{7},{8},{9}' -f $sw.Elapsed.TotalSeconds,$phase,[int]($p.PrivateMemorySize64/1MB),[int]($p.WorkingSet64/1MB),$p.Threads.Count,$p.HandleCount,[int]($io.rb/1MB),$io.ro,[int]($io.wb/1MB),$r) | Add-Content $Out
}
function Hold($phase,$sec) { $end=$sw.Elapsed.TotalSeconds+$sec; while($sw.Elapsed.TotalSeconds -lt $end){ Snap $phase; Start-Sleep -Milliseconds 400 } }
Add-Type -AssemblyName System.Windows.Forms
function Key($vk,$n,$delayMs=120,$phase='key') { $map=@{0x22='{PGDN}';0x23='{END}';0x24='{HOME}'}; for($i=0;$i -lt $n;$i++){ [void][Mem]::SetForegroundWindow($global:p.MainWindowHandle); [System.Windows.Forms.SendKeys]::SendWait($map[[int]$vk]); Start-Sleep -Milliseconds $delayMs; if($i % 5 -eq 0){ Snap $phase } } }

$proc = Start-Process -FilePath $Exe -ArgumentList ('"{0}"' -f $Pdf) -PassThru
Start-Sleep -Seconds 3
$global:p = Get-Process -Id $proc.Id
for($i=0;$i -lt 40 -and $global:p.MainWindowHandle -eq 0;$i++){ Start-Sleep -Milliseconds 500; $global:p.Refresh() }
"window=$($global:p.MainWindowHandle) title=$($global:p.MainWindowTitle)" | Out-File "$Out.log"
Hold 'load' 12
Hold 'idle-open' 8
Key 0x22 20 150 'pagedown-20'      # VK_NEXT
Hold 'after-scroll1' 5
Key 0x22 60 120 'pagedown-60'
Hold 'after-scroll2' 5
Key 0x23 1 100 'end'                # last page
Hold 'at-end' 6
Key 0x24 1 100 'home'
Hold 'at-home' 6
Hold 'idle-30s' 30
Hold 'idle-60s' 30
Snap 'final'
"done $($sw.Elapsed.TotalSeconds)" | Add-Content "$Out.log"



