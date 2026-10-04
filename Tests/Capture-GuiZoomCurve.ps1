param([string]$Pdf, [string]$Exe, [string]$OutDir, [string]$Tag, [string]$App, [string]$Mode = 'wheel', [int]$LoadWaitSec = 14)
# App = foxit | reader. Mode = wheel (3 notches in, 3 out, 800 ms apart) | slider (drag the zoom slider right then back).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$cp = New-Object System.CodeDom.Compiler.CompilerParameters; $cp.CompilerOptions = '/unsafe'
[void]$cp.ReferencedAssemblies.Add('System.dll'); [void]$cp.ReferencedAssemblies.Add('System.Drawing.dll')
Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Diagnostics; using System.Drawing; using System.Drawing.Drawing2D; using System.Drawing.Imaging; using System.IO; using System.Runtime.InteropServices; using System.Threading;
public static class Cur {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
  [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
  public static Rectangle Region(IntPtr h) { RECT r = Rect(h); int w = r.R - r.L, hh = r.B - r.T; int cw = Math.Min(1100, w - 160), ch = Math.Min(600, hh - 300);
    return new Rectangle(r.L + (w - cw) / 2, r.T + (hh - ch) / 2 + 20, cw, ch); }
  static void Wheel(int delta, bool ctrl) { if (ctrl) keybd_event(0x11, 0, 0, UIntPtr.Zero); mouse_event(0x0800, 0, 0, delta, UIntPtr.Zero); if (ctrl) keybd_event(0x11, 0, 2, UIntPtr.Zero); }
  // actions run on a worker thread after leadMs; the main thread captures frames as fast as it can for totalMs
  public static string Capture(Rectangle rc, string mode, int thumbX, int thumbY, int dragPx, int leadMs, int totalMs, string dir) {
    Directory.CreateDirectory(dir); foreach (var f in Directory.GetFiles(dir, "*.png")) File.Delete(f);
    var bmp = new Bitmap(rc.Width, rc.Height, PixelFormat.Format32bppRgb); var g = Graphics.FromImage(bmp);
    var frames = new List<Bitmap>(); var times = new List<double>(); var sw = Stopwatch.StartNew(); var marks = new List<string>();
    var t = new Thread(() => {
      Thread.Sleep(leadMs);
      if (mode == "wheel") {
        SetCursorPos(rc.X + rc.Width / 2, rc.Y + rc.Height / 2); Thread.Sleep(100);
        for (int i = 0; i < 3; i++) { marks.Add(string.Format("in{0}@{1:F0}", i, sw.Elapsed.TotalMilliseconds)); Wheel(120, true); Thread.Sleep(800); }
        for (int i = 0; i < 3; i++) { marks.Add(string.Format("out{0}@{1:F0}", i, sw.Elapsed.TotalMilliseconds)); Wheel(-120, true); Thread.Sleep(800); }
      } else if (mode == "slider2") {
        SetCursorPos(thumbX, thumbY); Thread.Sleep(150); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(80);
        var sw2 = Stopwatch.StartNew(); int n = 60; double stepMs = 16.0;
        marks.Add(string.Format("drag-right@{0:F0}", sw.Elapsed.TotalMilliseconds));
        for (int i = 1; i <= n; i++) { while (sw2.Elapsed.TotalMilliseconds < i * stepMs) Thread.Sleep(0); SetCursorPos(thumbX + dragPx * i / n, thumbY); }
        Thread.Sleep(400); marks.Add(string.Format("drag-left@{0:F0}", sw.Elapsed.TotalMilliseconds)); sw2.Restart();
        for (int i = 1; i <= n; i++) { while (sw2.Elapsed.TotalMilliseconds < i * stepMs) Thread.Sleep(0); SetCursorPos(thumbX + dragPx * (n - i) / n, thumbY); }
        Thread.Sleep(150); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
      } else if (mode == "jump") {
        SetCursorPos(thumbX, thumbY); Thread.Sleep(200); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(250);
        marks.Add(string.Format("jump-right@{0:F0}", sw.Elapsed.TotalMilliseconds)); SetCursorPos(thumbX + dragPx, thumbY); Thread.Sleep(2000);
        marks.Add(string.Format("jump-left@{0:F0}", sw.Elapsed.TotalMilliseconds)); SetCursorPos(thumbX, thumbY); Thread.Sleep(1500);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
      } else {
        SetCursorPos(thumbX, thumbY); Thread.Sleep(150); mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(80);
        marks.Add(string.Format("drag-right@{0:F0}", sw.Elapsed.TotalMilliseconds));
        int steps = 24; for (int i = 1; i <= steps; i++) { SetCursorPos(thumbX + dragPx * i / steps, thumbY); Thread.Sleep(40); }
        Thread.Sleep(300); marks.Add(string.Format("drag-left@{0:F0}", sw.Elapsed.TotalMilliseconds));
        for (int i = steps - 1; i >= 0; i--) { SetCursorPos(thumbX + dragPx * i / steps, thumbY); Thread.Sleep(40); }
        Thread.Sleep(150); mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
      }
    });
    t.Priority = ThreadPriority.Highest; timeBeginPeriod(1); t.Start();
    while (sw.Elapsed.TotalMilliseconds < totalMs) { g.CopyFromScreen(rc.Location, Point.Empty, rc.Size); frames.Add((Bitmap)bmp.Clone()); times.Add(sw.Elapsed.TotalMilliseconds); }
    t.Join(); timeEndPeriod(1);
    int w2 = rc.Width / 2, h2 = rc.Height / 2;
    for (int i = 0; i < frames.Count; i++) {
      using (var small = new Bitmap(w2, h2, PixelFormat.Format24bppRgb)) using (var gg = Graphics.FromImage(small)) {
        gg.InterpolationMode = InterpolationMode.HighQualityBilinear; gg.DrawImage(frames[i], 0, 0, w2, h2);
        small.Save(Path.Combine(dir, string.Format("f{0:D3}_{1:D5}.png", i, (int)times[i])), ImageFormat.Png); }
      frames[i].Dispose();
    }
    return string.Format("frames={0} fps={1:F0} marks={2}", frames.Count, frames.Count * 1000.0 / totalMs, string.Join(";", marks));
  }
}
'@ -CompilerParameters $cp

$proc = Start-Process -FilePath $Exe -ArgumentList ('"{0}"' -f $Pdf) -PassThru
Start-Sleep -Seconds 3
$p = Get-Process -Id $proc.Id
for ($i = 0; $i -lt 40 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
$h = $p.MainWindowHandle
[void][Cur]::ShowWindow($h, 3); [void][Cur]::SetForegroundWindow($h); Start-Sleep -Seconds $LoadWaitSec
foreach ($k in 1..30) { [System.Windows.Forms.SendKeys]::SendWait('{PGDN}'); Start-Sleep -Milliseconds 150 }
Start-Sleep -Seconds 3
$r = [Cur]::Rect($h); $rc = [Cur]::Region($h)
if ($App -eq 'foxit') { $tx = $r.R - 113; $ty = $r.B - 23 } else { $tx = $r.R - 202; $ty = $r.B - 15 }
[void][Cur]::SetForegroundWindow($h)
$res = [Cur]::Capture($rc, $Mode, $tx, $ty, 60, 400, 8200, (Join-Path $OutDir "$Tag-$Mode"))
"$Tag $Mode window=$($r.L),$($r.T),$($r.R),$($r.B) thumb=$tx,$ty : $res" | Set-Content (Join-Path $OutDir "$Tag-$Mode.log")
[void]$p.CloseMainWindow(); Start-Sleep -Seconds 4; if (-not $p.HasExited) { $p.Refresh(); [void]$p.CloseMainWindow(); Start-Sleep 3 }


