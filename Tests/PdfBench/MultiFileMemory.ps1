param([string]$Tag = 'multi', [int]$Copies = 5, [int]$Rounds = 1, [int]$IdleSeconds = 40)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class W { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, UIntPtr e);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); }
'@
$exe = 'C:\Users\condu\source\repos\xuantoi2012\XTPdfReader\bin\Release\net10.0-windows10.0.19041.0\XTPdfMergeApp.exe'
$big = 'C:\Users\condu\Desktop\03. QUYEN 2.2 - TNM, TNT, HKT, CAY XANH, CHIEU SANG, TCTC.pdf'
$small = 'C:\Users\condu\Desktop\Tập 3 - Bản vẽ thiết kế cơ sở.pdf'
$dir = "$env:TEMP\multi"; New-Item -ItemType Directory -Force $dir | Out-Null
$files = @()
for ($i = 1; $i -le $Copies; $i++) { $p = "$dir\big$i.pdf"; if (-not (Test-Path $p)) { Copy-Item $big $p }; $files += $p }
$files += $small
$env:XTPDF_DEBUG_LOG = "$env:TEMP\$Tag.log"
$series = New-Object System.Collections.ArrayList
$sw = [Diagnostics.Stopwatch]::StartNew()
function Mem($label) {
    $p = Get-Process -Name XTPdfMergeApp -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { return }
    $c = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($p.Id)" -ErrorAction SilentlyContinue
    $row = "{0,6:0}s {1,-28} private={2,5} MB  privateWS={3,5} MB" -f $sw.Elapsed.TotalSeconds, $label, [math]::Round($p.PrivateMemorySize64 / 1MB), [math]::Round($c.WorkingSetPrivate / 1MB)
    [void]$series.Add($row); $row
}
$args2 = ($files | ForEach-Object { "`"$_`"" }) -join ' '
$p = Start-Process $exe -ArgumentList $args2 -PassThru
$p.WaitForInputIdle(30000) | Out-Null
Start-Sleep 3
$p.Refresh(); [W]::MoveWindow($p.MainWindowHandle, 100, 100, 1200, 700, $true) | Out-Null; [W]::ShowWindow($p.MainWindowHandle, 3) | Out-Null; [W]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
$sh = New-Object -ComObject WScript.Shell; [void]$sh.AppActivate($p.Id)
foreach ($t in 5, 10, 15, 20, 30) { Start-Sleep -Seconds ($t - [math]::Min($t, $sw.Elapsed.TotalSeconds)); Mem "open +${t}s" }
$scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $scr.Width, $scr.Height; $g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, $bmp.Size); $bmp.Save("$env:TEMP\${Tag}_tabs.png")
# continuous mode
[void][W]::SetCursorPos(294, 68); Start-Sleep -Milliseconds 300; [W]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); [W]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep 2
for ($r = 1; $r -le $Rounds; $r++) {
    for ($i = 0; $i -lt $files.Count; $i++) {
        [void][W]::SetCursorPos(@(45,138,230,322,414,575)[$i], 146); Start-Sleep -Milliseconds 200
        [W]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); [W]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 1500
        [void][W]::SetCursorPos([int]($scr.Width * 0.55), [int]($scr.Height * 0.5))
        [System.Windows.Forms.SendKeys]::SendWait('{END}'); Start-Sleep 2
        [System.Windows.Forms.SendKeys]::SendWait('{HOME}'); Start-Sleep 1
        for ($k = 0; $k -lt 12; $k++) { [System.Windows.Forms.SendKeys]::SendWait('{PGDN}'); Start-Sleep -Milliseconds 300 }
        for ($k = 0; $k -lt 30; $k++) { [W]::mouse_event(0x0800, 0, 0, -120, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30 }
        Start-Sleep 2
        Mem "round $r tab $($i + 1)"
    }
}
for ($t = 10; $t -le $IdleSeconds; $t += 10) { Start-Sleep 10; Mem "idle +${t}s" }
$p.CloseMainWindow() | Out-Null; Start-Sleep 6
Get-Process -Name XTPdfMergeApp -ErrorAction SilentlyContinue | Stop-Process -Force
$env:XTPDF_DEBUG_LOG = $null
