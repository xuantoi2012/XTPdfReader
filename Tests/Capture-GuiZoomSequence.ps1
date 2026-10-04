param([string]$Pdf, [string]$Exe, [string]$OutDir, [string]$Tag, [int]$Notches = 8, [int]$IntervalMs = 30, [int]$LoadWaitSec = 14, [string]$Mode = 'zoom')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$cp = New-Object System.CodeDom.Compiler.CompilerParameters; $cp.CompilerOptions = '/unsafe'
[void]$cp.ReferencedAssemblies.Add('System.dll'); [void]$cp.ReferencedAssemblies.Add('System.Drawing.dll')
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Diagnostics; using System.Drawing; using System.Drawing.Imaging; using System.IO; using System.Runtime.InteropServices; using System.Threading;
public static class Seq {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static Rectangle Region(IntPtr h) { RECT r; GetWindowRect(h, out r); int w = r.R - r.L, hh = r.B - r.T; int cw = Math.Min(1000, w - 120), ch = Math.Min(560, hh - 260);
    return new Rectangle(r.L + (w - cw) / 2, r.T + (hh - ch) / 2 + 60, cw, ch); }
  public static void Center(Rectangle rc) { SetCursorPos(rc.X + rc.Width / 2, rc.Y + rc.Height / 2); }
  // Rolls the wheel (with Ctrl) on a worker thread starting after leadMs, and captures the region as fast as possible for totalMs.
  public static string Capture(Rectangle rc, int notches, int delta, int intervalMs, int leadMs, int totalMs, string dir, bool ctrl) {
    Directory.CreateDirectory(dir);
    foreach (var f in Directory.GetFiles(dir, "*.png")) File.Delete(f);
    var bmp = new Bitmap(rc.Width, rc.Height, PixelFormat.Format32bppRgb); var g = Graphics.FromImage(bmp);
    var frames = new List<Bitmap>(); var times = new List<double>();
    var sw = Stopwatch.StartNew(); double wheelStart = -1;
    var t = new Thread(() => { Thread.Sleep(leadMs); wheelStart = sw.Elapsed.TotalMilliseconds;
      for (int i = 0; i < notches; i++) { if (ctrl) keybd_event(0x11, 0, 0, UIntPtr.Zero); mouse_event(0x0800, 0, 0, delta, UIntPtr.Zero); if (ctrl) keybd_event(0x11, 0, 2, UIntPtr.Zero); Thread.Sleep(intervalMs); } });
    t.Start();
    while (sw.Elapsed.TotalMilliseconds < totalMs) { g.CopyFromScreen(rc.Location, Point.Empty, rc.Size); frames.Add((Bitmap)bmp.Clone()); times.Add(sw.Elapsed.TotalMilliseconds); }
    t.Join();
    for (int i = 0; i < frames.Count; i++) { frames[i].Save(Path.Combine(dir, string.Format("f{0:D3}_{1:D5}.png", i, (int)times[i])), ImageFormat.Png); frames[i].Dispose(); }
    return string.Format("frames={0} wheelStartMs={1:F0} fps={2:F0}", frames.Count, wheelStart, frames.Count * 1000.0 / totalMs);
  }
}
'@ -CompilerParameters $cp

$proc = Start-Process -FilePath $Exe -ArgumentList ('"{0}"' -f $Pdf) -PassThru
Start-Sleep -Seconds 3
$p = Get-Process -Id $proc.Id
for ($i = 0; $i -lt 40 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
$h = $p.MainWindowHandle
[void][Seq]::SetForegroundWindow($h); Start-Sleep -Seconds $LoadWaitSec
foreach ($k in 1..30) { [System.Windows.Forms.SendKeys]::SendWait('{PGDN}'); Start-Sleep -Milliseconds 150 }
Start-Sleep -Seconds 3
$rc = [Seq]::Region($h); [Seq]::Center($rc)
$log = @()
[void][Seq]::SetForegroundWindow($h)
if ($Mode -eq 'scroll') {
  [void][Seq]::SetForegroundWindow($h)
  $log += "$Tag scroll: " + [Seq]::Capture($rc, 40, -120, 12, 250, 2600, (Join-Path $OutDir "$Tag-scroll"), $false)
} else {
  [void][Seq]::SetForegroundWindow($h)
  $log += "$Tag zoomroll-in: " + [Seq]::Capture($rc, 40, 120, 20, 250, 2200, (Join-Path $OutDir "$Tag-zoomin40"), $true)
  Start-Sleep -Seconds 2
  [void][Seq]::SetForegroundWindow($h)
  $log += "$Tag zoomroll-out: " + [Seq]::Capture($rc, 40, -120, 20, 250, 2200, (Join-Path $OutDir "$Tag-zoomout40"), $true)
}$log | Set-Content (Join-Path $OutDir "$Tag.log")
[void]$p.CloseMainWindow(); Start-Sleep -Seconds 4; if (-not $p.HasExited) { $p.Refresh(); [void]$p.CloseMainWindow(); Start-Sleep 3 }
"$Tag exited=$($p.HasExited)" | Add-Content (Join-Path $OutDir "$Tag.log")

