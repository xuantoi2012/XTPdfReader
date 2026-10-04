param([string]$Pdf, [string]$Exe = 'C:\Program Files (x86)\Foxit Software\Foxit PhantomPDF\FoxitPhantomPDF.exe', [string]$OutDir, [string]$Tag = 'foxit', [int]$LoadWaitSec = 25,
  [string]$Mode = 'zoomin', [int]$Notches = 40, [int]$IntervalMs = 30)
# Sampling profiler for a 32-bit viewer: every ~2 ms suspend its UI thread for a moment (no debugger attached), read EIP/ESP and 4 KB of stack, resume.
# Output: <Tag>-<mode>.samples.csv (t_ms, eip, esp, module+offset of eip) and <Tag>-<mode>.stack.bin-style hex columns for return-address recovery.
# Used only to see where the viewer's UI thread spends its time while a Ctrl+wheel zoom roll or a scroll runs.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$cp = New-Object System.CodeDom.Compiler.CompilerParameters
[void]$cp.ReferencedAssemblies.Add('System.dll'); [void]$cp.ReferencedAssemblies.Add('System.Core.dll')
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Diagnostics; using System.IO; using System.Linq; using System.Runtime.InteropServices; using System.Text; using System.Threading;
public static class Smp {
  [DllImport("kernel32.dll")] static extern IntPtr OpenThread(uint access, bool inherit, uint tid);
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
  [DllImport("kernel32.dll")] static extern uint SuspendThread(IntPtr h);
  [DllImport("kernel32.dll")] static extern uint ResumeThread(IntPtr h);
  [DllImport("kernel32.dll")] static extern bool Wow64GetThreadContext(IntPtr h, IntPtr ctx);
  [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, byte[] buf, int size, out int read);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static void Center(IntPtr h) { RECT r; GetWindowRect(h, out r); SetCursorPos((r.L + r.R) / 2, (r.T + r.B) / 2 + 40); }
  public static uint UiThread(IntPtr hwnd) { uint pid; return GetWindowThreadProcessId(hwnd, out pid); }
  public static string Run(int pid, uint tid, int notches, int gapMs, bool ctrl, int delta, int totalMs, string csv) {
    timeBeginPeriod(1);
    var p = Process.GetProcessById(pid);
    var mods = p.Modules.Cast<ProcessModule>().Select(m => new { Name = m.ModuleName, Base = (long)m.BaseAddress, Size = m.ModuleMemorySize }).OrderBy(m => m.Base).ToList();
    IntPtr hp = OpenProcess(0x0410, false, (uint)pid), ht = OpenThread(0x001A, false, tid); // SUSPEND_RESUME|GET_CONTEXT|QUERY_INFORMATION ; VM_READ|QUERY
    if (ht == IntPtr.Zero || hp == IntPtr.Zero) return "open failed";
    IntPtr ctx = Marshal.AllocHGlobal(716); var sw = Stopwatch.StartNew();
    var th = new Thread(() => { Thread.Sleep(300);
      for (int i = 0; i < notches; i++) { if (ctrl) keybd_event(0x11, 0, 0, UIntPtr.Zero); mouse_event(0x0800, 0, 0, delta, UIntPtr.Zero); if (ctrl) keybd_event(0x11, 0, 2, UIntPtr.Zero); Thread.Sleep(gapMs); } });
    th.Start();
    var rows = new List<string>(); rows.Add("t_ms,eip,esp,where");
    var stacks = new List<string>();
    int n = 0;
    while (sw.Elapsed.TotalMilliseconds < totalMs) {
      Thread.Sleep(2);
      byte[] stack = new byte[4096]; int read = 0; int eip = 0, esp = 0; double t = sw.Elapsed.TotalMilliseconds;
      if (SuspendThread(ht) == uint.MaxValue) continue;
      try {
        for (int i = 0; i < 716; i++) Marshal.WriteByte(ctx, i, 0);
        Marshal.WriteInt32(ctx, 0, 0x10003);
        if (Wow64GetThreadContext(ht, ctx)) {
          eip = Marshal.ReadInt32(ctx, 184); esp = Marshal.ReadInt32(ctx, 196);
          ReadProcessMemory(hp, (IntPtr)(uint)esp, stack, stack.Length, out read);
        }
      } finally { ResumeThread(ht); }
      if (eip == 0) continue;
      long e = (uint)eip; string where = "?";
      foreach (var m in mods) if (e >= m.Base && e < m.Base + m.Size) { where = m.Name + "+" + (e - m.Base).ToString("x"); break; }
      rows.Add(string.Format("{0:F1},{1:x},{2:x},{3}", t, (uint)eip, (uint)esp, where));
      var sb = new StringBuilder(); // dwords of the stack that fall inside any module: potential return addresses (rebased later)
      for (int i = 0; i + 4 <= read; i += 4) { long v = BitConverter.ToUInt32(stack, i); foreach (var m in mods) if (v >= m.Base && v < m.Base + m.Size) { sb.Append(m.Name).Append('+').Append((v - m.Base).ToString("x")).Append(';'); break; } }
      stacks.Add(sb.ToString()); n++;
    }
    th.Join(); CloseHandle(ht); CloseHandle(hp);
    File.WriteAllLines(csv, rows); File.WriteAllLines(csv + ".stacks", stacks);
    return string.Format("samples={0} exeBase={1:x} modules={2}", n, mods.First(m => m.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Base, mods.Count);
  }
}
'@ -CompilerParameters $cp

New-Item -ItemType Directory -Force $OutDir | Out-Null
$proc = Start-Process -FilePath $Exe -ArgumentList ('"{0}"' -f $Pdf) -PassThru
Start-Sleep -Seconds 3
$p = Get-Process -Id $proc.Id
for ($i = 0; $i -lt 40 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
$h = $p.MainWindowHandle
[void][Smp]::SetForegroundWindow($h); Start-Sleep -Seconds $LoadWaitSec
foreach ($k in 1..30) { [System.Windows.Forms.SendKeys]::SendWait('{PGDN}'); Start-Sleep -Milliseconds 150 }
Start-Sleep -Seconds 3
[Smp]::Center($h); [void][Smp]::SetForegroundWindow($h)
$tid = [Smp]::UiThread($h)
$log = @()
if ($Mode -eq 'scroll') { $log += "$Tag scroll tid=$tid : " + [Smp]::Run($p.Id, $tid, $Notches, $IntervalMs, $false, -120, 2500, (Join-Path $OutDir "$Tag-scroll.csv")) }
else {
  $log += "$Tag zoomin tid=$tid : " + [Smp]::Run($p.Id, $tid, $Notches, $IntervalMs, $true, 120, 2500, (Join-Path $OutDir "$Tag-zoomin.csv"))
  Start-Sleep -Seconds 2; [void][Smp]::SetForegroundWindow($h)
  $log += "$Tag zoomout tid=$tid : " + [Smp]::Run($p.Id, $tid, $Notches, $IntervalMs, $true, -120, 2500, (Join-Path $OutDir "$Tag-zoomout.csv"))
}
$log | Set-Content (Join-Path $OutDir "$Tag.log")
[void]$p.CloseMainWindow(); Start-Sleep -Seconds 4; if (-not $p.HasExited) { $p.Refresh(); [void]$p.CloseMainWindow(); Start-Sleep 3 }
$log
