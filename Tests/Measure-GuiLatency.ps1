param([string]$Pdf, [string]$Out, [string]$Exe, [int]$LoadWaitSec = 14, [string]$Mode = 'std')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices; using System.Diagnostics;
public static class Lat {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int data, UIntPtr extra);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static Rectangle Region(IntPtr h) { RECT r; GetWindowRect(h, out r); int w = r.R - r.L, hh = r.B - r.T; int cw = 640, ch = 420;
    return new Rectangle(r.L + (w - cw) / 2 + 40, r.T + (hh - ch) / 2 + 30, cw, ch); }
  static Bitmap bmp; static Graphics g;
  public static byte[] Grab(Rectangle rc) {
    if (bmp == null || bmp.Width != rc.Width || bmp.Height != rc.Height) { bmp = new Bitmap(rc.Width, rc.Height, PixelFormat.Format32bppRgb); g = Graphics.FromImage(bmp); }
    g.CopyFromScreen(rc.Location, Point.Empty, rc.Size);
    var d = bmp.LockBits(new Rectangle(0, 0, rc.Width, rc.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
    int step = 1; int nx = rc.Width / step, ny = rc.Height / step; var o = new byte[nx * ny];
    unsafe { byte* p = (byte*)d.Scan0; for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++) { byte* q = p + (y * step) * d.Stride + (x * step) * 4; o[y * nx + x] = (byte)((q[0] + q[1] * 2 + q[2]) >> 2); } }
    bmp.UnlockBits(d); return o; }
  public static int Diff(byte[] a, byte[] b) { int n = 0; for (int i = 0; i < a.Length; i++) { int d = a[i] - b[i]; if (d > 24 || d < -24) n++; } return n; }
  public static int Diff(byte[] a, byte[] b, int tol) { int n = 0; for (int i = 0; i < a.Length; i++) { int d = a[i] - b[i]; if (d > tol || d < -tol) n++; } return n; }
  // Captures windowMs after the action, takes the LAST frame as the finished image and returns "first_ms,ready_ms,frames"
  // first = first frame that differs from the pre-action frame; ready = start of the final stretch of frames equal to the finished image.
  public static string Measure(Rectangle rc, Action act, int quietMs, int windowMs) {
    var baseline = Grab(rc); var sw = Stopwatch.StartNew(); act();
    var frames = new System.Collections.Generic.List<byte[]>(); var times = new System.Collections.Generic.List<double>();
    while (sw.Elapsed.TotalMilliseconds < windowMs) { frames.Add(Grab(rc)); times.Add(sw.Elapsed.TotalMilliseconds); }
    var fin = frames[frames.Count - 1]; int thr = 12, tol = 16;
    double first = -1; int firstIdx = -1; for (int i = 0; i < frames.Count; i++) if (Diff(frames[i], baseline, tol) > thr) { first = times[i]; firstIdx = i; break; }
    int ready = frames.Count - 1; while (ready > 0 && Diff(frames[ready - 1], fin, tol) <= thr) ready--;
    // bursts = separate visual updates (changes more than 30 ms apart); jump = samples that still differ between the first changed frame and the finished image
    int bursts = 0; double lastChange = -1000; for (int i = 1; i < frames.Count; i++) if (Diff(frames[i], frames[i - 1], 6) > 3) { if (times[i] - lastChange > 30) bursts++; lastChange = times[i]; }
    int jump = firstIdx >= 0 ? Diff(frames[firstIdx], fin, tol) : 0; int maxStep = 0; for (int i = 1; i < frames.Count; i++) { if (times[i] < 70) continue; int d = Diff(frames[i], frames[i - 1], tol); if (d > maxStep) maxStep = d; }
    return string.Format("{0:F0},{1:F0},{2},{3},{4},{5}", first, times[ready], frames.Count, bursts, jump, maxStep);  }
}
'@ -ReferencedAssemblies @('System.dll', 'System.Drawing.dll') -CompilerOptions '/unsafe'

function Wheel($delta,$ctrl) { if($ctrl){[Lat]::keybd_event(0x11,0,0,[UIntPtr]::Zero)}; [Lat]::mouse_event(0x0800,0,0,$delta,[UIntPtr]::Zero); if($ctrl){[Lat]::keybd_event(0x11,0,2,[UIntPtr]::Zero)} }
$proc = Start-Process -FilePath $Exe -ArgumentList ('"{0}"' -f $Pdf) -PassThru
Start-Sleep -Seconds 3
$p = Get-Process -Id $proc.Id
for($i=0;$i -lt 40 -and $p.MainWindowHandle -eq 0;$i++){ Start-Sleep -Milliseconds 500; $p.Refresh() }
$h = $p.MainWindowHandle
[void][Lat]::SetForegroundWindow($h); Start-Sleep -Seconds $LoadWaitSec
$rc = [Lat]::Region($h)
[Lat]::SetCursorPos($rc.X + [int]($rc.Width/2), $rc.Y + [int]($rc.Height/2)) | Out-Null
$rows = @('step,first_change_ms,settle_ms,frames,bursts,jump_samples,max_step')
function Step($name,[scriptblock]$act,$quiet=250,$max=2500){ [void][Lat]::SetForegroundWindow($h); $r=[Lat]::Measure($rc,[Action]$act,$quiet,$max); $script:rows += "$name,$r"; Start-Sleep -Milliseconds 700 }
$send={ param($k) [System.Windows.Forms.SendKeys]::SendWait($k) }

if($Mode -eq 'zoom'){
foreach($k in 1..30){ [System.Windows.Forms.SendKeys]::SendWait('{PGDN}'); Start-Sleep -Milliseconds 150 }; Start-Sleep -Seconds 3
foreach($i in 1..14){ Step "zin-$i" { Wheel 120 $true } 250 2500 }
foreach($i in 1..14){ Step "zout-$i" { Wheel -120 $true } 250 2500 }
} elseif($Mode -eq 'pan'){
function Notches($n,$d){ for($k=0;$k -lt $n;$k++){ Wheel $d $true; Start-Sleep -Milliseconds 25 } }
function PanV($n,$d,$gap){ for($k=0;$k -lt $n;$k++){ Wheel $d $false; Start-Sleep -Milliseconds $gap } }
function PanH($n,$d,$gap){ for($k=0;$k -lt $n;$k++){ [Lat]::keybd_event(0x10,0,0,[UIntPtr]::Zero); [Lat]::mouse_event(0x0800,0,0,-$d,[UIntPtr]::Zero); [Lat]::keybd_event(0x10,0,2,[UIntPtr]::Zero); Start-Sleep -Milliseconds $gap } }
Step 'pan-zoomin' { Notches 30 120 } 250 5000
# settle_ms - (notches*gap) is the time the picture kept changing after the hand stopped (the blurry-to-sharp tail)
Step 'panH-right-12x40' { PanH 12 120 40 } 250 4000
Step 'panH-left-12x40' { PanH 12 -120 40 } 250 4000
Step 'panV-down-12x40' { PanV 12 -120 40 } 250 4000
Step 'panV-up-12x40' { PanV 12 120 40 } 250 4000
Step 'panH-right-30x25' { PanH 30 120 25 } 250 4000
Step 'panV-down-30x25' { PanV 30 -120 25 } 250 4000
Step 'panH-left-30x25' { PanH 30 -120 25 } 250 4000
Step 'panV-up-30x25' { PanV 30 120 25 } 250 4000
Step 'panH-right-80x10' { PanH 80 120 10 } 250 5000
Step 'panH-left-80x10' { PanH 80 -120 10 } 250 5000
Step 'panV-down-80x10' { PanV 80 -120 10 } 250 5000
Step 'panV-up-80x10' { PanV 80 120 10 } 250 5000
Step 'panH-right-10x150' { PanH 10 120 150 } 250 5000
Step 'panH-left-10x150' { PanH 10 -120 150 } 250 5000
} elseif($Mode -eq 'deep'){
function Notches($n,$d){ for($k=0;$k -lt $n;$k++){ Wheel $d $true; Start-Sleep -Milliseconds 25 } }
Step 'deep-zoomin-10'  { Notches 10 120 } 250 4000
Step 'deep-zoomin-+10' { Notches 10 120 } 250 4000
Step 'deep-zoomin-+10b' { Notches 10 120 } 250 4000
Step 'deep-zoomin-+10c' { Notches 10 120 } 250 4000
Step 'deep-pan-1' { Wheel -120 $false } 250 3000
Step 'deep-pan-2' { Wheel -120 $false } 250 3000
Step 'deep-pan-3' { Wheel -120 $false } 250 3000
Step 'deep-pan-4' { Wheel -120 $false } 250 3000
Step 'deep-zoomin-+5' { Notches 5 120 } 250 4000
Step 'deep-zoomout-20' { Notches 20 -120 } 250 4000
Step 'deep-zoomout-20b' { Notches 20 -120 } 250 4000
Step 'deep-revisit-end' { [System.Windows.Forms.SendKeys]::SendWait('{END}') } 250 4000
Step 'deep-revisit-home' { [System.Windows.Forms.SendKeys]::SendWait('{HOME}') } 250 4000
} else {
Step 'newpage-pgdn-1'  { [System.Windows.Forms.SendKeys]::SendWait('{PGDN}') }
Step 'newpage-pgdn-2'  { [System.Windows.Forms.SendKeys]::SendWait('{PGDN}') }
Step 'newpage-pgdn-3'  { [System.Windows.Forms.SendKeys]::SendWait('{PGDN}') }
Step 'newpage-jump-end'  { [System.Windows.Forms.SendKeys]::SendWait('{END}') } 250 4000
Step 'revisit-home'      { [System.Windows.Forms.SendKeys]::SendWait('{HOME}') }
Step 'revisit-end-again' { [System.Windows.Forms.SendKeys]::SendWait('{END}') } 250 4000
Step 'revisit-home-2'    { [System.Windows.Forms.SendKeys]::SendWait('{HOME}') }
foreach($i in 1..3){ Step "zoom-in-$i"  { Wheel 120 $true } 250 3000 }
foreach($i in 4..6){ Step "zoom-in-$i"  { Wheel 120 $true } 250 3000 }
foreach($i in 7..9){ Step "zoom-in-$i"  { Wheel 120 $true } 250 3000 }
foreach($i in 1..3){ Step "pan-down-$i" { Wheel -120 $false } 250 3000 }
foreach($i in 1..9){ Step "zoom-out-$i" { Wheel -120 $true } 250 3000 }

}
$rows | Set-Content $Out
$p.Refresh(); "peak private MB: $([int]($p.PrivateMemorySize64/1MB))" | Add-Content "$Out.log"
[void]$p.CloseMainWindow(); Start-Sleep -Seconds 4; if(-not $p.HasExited){ $p.Refresh(); [void]$p.CloseMainWindow(); Start-Sleep 3 }
"exited=$($p.HasExited)" | Add-Content "$Out.log"










