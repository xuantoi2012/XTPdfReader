param([string]$Pdf, [string]$Exe, [string]$Tag, [string]$OutDir, [int]$LoadWaitSec = 14, [int]$Notches = 40, [int]$IntervalMs = 30, [string]$Mode = 'zoomin')
# Passive CPU profile per thread (QueryThreadCycleTime, every 10 ms) of a PDF viewer while a Ctrl+wheel roll or a scroll runs: how many threads work at
# once and how much CPU one roll costs. Reads only; used to compare how Foxit and Reader spread render work over cores.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$cp = New-Object System.CodeDom.Compiler.CompilerParameters
[void]$cp.ReferencedAssemblies.Add('System.dll'); [void]$cp.ReferencedAssemblies.Add('System.Core.dll')
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Diagnostics; using System.Linq; using System.Runtime.InteropServices; using System.Threading;
public static class Prof {
  [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
  [DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr h, out ulong cycles);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static void Center(IntPtr h) { RECT r; GetWindowRect(h, out r); SetCursorPos((r.L + r.R) / 2, (r.T + r.B) / 2 + 40); }
  static Dictionary<int, ulong> Snap(Process p, Dictionary<int, IntPtr> handles) {
    var d = new Dictionary<int, ulong>();
    p.Refresh();
    foreach (ProcessThread t in p.Threads) {
      IntPtr h;
      if (!handles.TryGetValue(t.Id, out h)) { h = OpenThread(0x0800, false, (uint)t.Id); handles[t.Id] = h; }
      ulong c; if (h != IntPtr.Zero && QueryThreadCycleTime(h, out c)) d[t.Id] = c;
    }
    return d;
  }
  public static string Run(int pid, int notches, int gapMs, bool ctrl, int delta, int totalMs, string csv) {
    var p = Process.GetProcessById(pid); var handles = new Dictionary<int, IntPtr>();
    var sw = Stopwatch.StartNew();
    var t = new Thread(() => { Thread.Sleep(300);
      for (int i = 0; i < notches; i++) { if (ctrl) keybd_event(0x11, 0, 0, UIntPtr.Zero); mouse_event(0x0800, 0, 0, delta, UIntPtr.Zero); if (ctrl) keybd_event(0x11, 0, 2, UIntPtr.Zero); Thread.Sleep(gapMs); } });
    var times = new List<double>(); var snaps = new List<Dictionary<int, ulong>>();
    times.Add(0); snaps.Add(Snap(p, handles));
    t.Start();
    while (sw.Elapsed.TotalMilliseconds < totalMs) { Thread.Sleep(10); times.Add(sw.Elapsed.TotalMilliseconds); snaps.Add(Snap(p, handles)); }
    t.Join();
    // threshold: a thread "works" in an interval when it used > 0.2 of a 3 GHz core over ~10 ms
    double thr = 6e6; var perThread = new Dictionary<int, double>(); var conc = new List<int>(); double total = 0;
    var lines = new List<string>(); lines.Add("t_ms,active_threads,cycles_m");
    for (int i = 1; i < snaps.Count; i++) {
      int active = 0; double sum = 0;
      foreach (var kv in snaps[i]) { ulong prev; if (!snaps[i - 1].TryGetValue(kv.Key, out prev)) prev = kv.Value; double dc = kv.Value - prev; if (dc < 0) dc = 0; sum += dc; if (dc > thr) active++; double acc; perThread.TryGetValue(kv.Key, out acc); perThread[kv.Key] = acc + dc; }
      conc.Add(active); total += sum; lines.Add(string.Format("{0:F0},{1},{2:F1}", times[i], active, sum / 1e6));
    }
    System.IO.File.WriteAllLines(csv, lines);
    var busy = conc.Where(c => c > 0).ToList();
    var top = perThread.OrderByDescending(kv => kv.Value).Take(8).Select(kv => string.Format("{0}:{1:F0}M({2:P0})", kv.Key, kv.Value / 1e6, kv.Value / Math.Max(1, total)));
    foreach (var h in handles.Values) CloseHandle(h);
    return string.Format("totalCycles={0:F0}M intervals={1} avgActiveWhenBusy={2:F2} maxActive={3} activeGE2={4} activeGE3={5} threads={6} top=[{7}]",
      total / 1e6, conc.Count, busy.Count == 0 ? 0 : busy.Average(), conc.Count == 0 ? 0 : conc.Max(), conc.Count(c => c >= 2), conc.Count(c => c >= 3), perThread.Count, string.Join(" ", top));
  }
}
'@ -CompilerParameters $cp

New-Item -ItemType Directory -Force $OutDir | Out-Null
$proc = Start-Process -FilePath $Exe -ArgumentList ('"{0}"' -f $Pdf) -PassThru
Start-Sleep -Seconds 3
$p = Get-Process -Id $proc.Id
for ($i = 0; $i -lt 40 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
$h = $p.MainWindowHandle
[void][Prof]::SetForegroundWindow($h); Start-Sleep -Seconds $LoadWaitSec
foreach ($k in 1..30) { [System.Windows.Forms.SendKeys]::SendWait('{PGDN}'); Start-Sleep -Milliseconds 150 }
Start-Sleep -Seconds 3
[Prof]::Center($h); [void][Prof]::SetForegroundWindow($h)
$log = @()
if ($Mode -eq 'scroll') { $log += "$Tag scroll: " + [Prof]::Run($p.Id, $Notches, $IntervalMs, $false, -120, 2500, (Join-Path $OutDir "$Tag-scroll.csv")) }
else {
  $log += "$Tag zoomin: " + [Prof]::Run($p.Id, $Notches, $IntervalMs, $true, 120, 2500, (Join-Path $OutDir "$Tag-zoomin.csv"))
  Start-Sleep -Seconds 2; [void][Prof]::SetForegroundWindow($h)
  $log += "$Tag zoomout: " + [Prof]::Run($p.Id, $Notches, $IntervalMs, $true, -120, 2500, (Join-Path $OutDir "$Tag-zoomout.csv"))
}
$log | Set-Content (Join-Path $OutDir "$Tag.log")
[void]$p.CloseMainWindow(); Start-Sleep -Seconds 4; if (-not $p.HasExited) { $p.Refresh(); [void]$p.CloseMainWindow(); Start-Sleep 3 }
$log
