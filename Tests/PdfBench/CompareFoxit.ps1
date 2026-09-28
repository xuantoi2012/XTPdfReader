# Do do tre mo file / nhay trang / cuon bang chup man hinh, dung chung cho Foxit va XTPdfMergeApp (chi chup man hinh chinh).
# Vi du: .\CompareFoxit.ps1 -Exe <XTPdfMergeApp.exe> -Tag mine -ProcName XTPdfMergeApp [-PreEndWait 25]
#        .\CompareFoxit.ps1 -Exe 'C:\Program Files (x86)\Foxit Software\Foxit PhantomPDF\FoxitPhantomPDF.exe' -Tag foxit
# Cot ket qua: title_ms, doc_area_first_draw_ms, open_settle_ms, end_settle_ms, home_settle_ms, pgdn_settle_after_last_ms, zoom_in/out_settle_ms (gom 720 ms cuon chuot), wheel40_settle_after_last_ms, ram_private/private_ws/workingset_mb, idle_* (sau -IdleWait s rảnh). Thêm: -PdfPath <file>, -Continuous (chuyển Cuộn liên tục), -IdleWait <s>.
# Anh chup nam o %TEMP%\shots_<Tag>. Tra ket qua chi tin khi da xem anh: man hinh 'Dang tai trang' khong doi van tinh la yen lang.
param([string]$Exe, [string]$Tag, [string]$TitleMatch = 'QUYEN', [int]$Scroll = 10, [string]$ProcName = '', [int]$PreEndWait = 0, [string]$PdfPath = '', [switch]$Continuous, [int]$IdleWait = 0)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class W { [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool r);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, int dx, int dy, int d, UIntPtr e);
 [DllImport("user32.dll")] public static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y); }
'@
$dir = Get-ChildItem -Path 'P:\01-PIP\032-Tinh lo 991 noi dai\07-TKBVTC\C-Design\01-Publication\2026-09*\PDF' -Directory | Select-Object -First 1
$f = (Get-ChildItem -LiteralPath $dir.FullName -Filter '03. QUYEN 2.2*.pdf' | Select-Object -First 1).FullName
if ($PdfPath) { $f = $PdfPath }
$out = "$env:TEMP\shots_$Tag"; New-Item -ItemType Directory -Force $out | Out-Null
$scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$W = 96; $H = 54
$small = New-Object System.Drawing.Bitmap $W, $H
$gs = [System.Drawing.Graphics]::FromImage($small); $gs.InterpolationMode = 'HighQualityBilinear'
$full = New-Object System.Drawing.Bitmap $scr.Width, $scr.Height
$gf = [System.Drawing.Graphics]::FromImage($full)
$prev = $null; $sw = [Diagnostics.Stopwatch]::StartNew()
function Snap {
    $gf.CopyFromScreen($scr.Left, $scr.Top, 0, 0, $full.Size)
    $gs.DrawImage($full, 0, 0, $W, $H)
    $a = New-Object 'double[]' ($W * $H); $ink = 0
    for ($y = 0; $y -lt $H - 3; $y++) { for ($x = 0; $x -lt $W; $x++) { $c = $small.GetPixel($x, $y); $g = ($c.R + $c.G + $c.B) / 3.0; $a[$y * $W + $x] = $g; if ($g -lt 235) { $ink++ } } }
    $reg = New-Object 'double[]' (75 * 39); $k = 0
    for ($y = 10; $y -lt 49; $y++) { for ($x = 20; $x -lt 95; $x++) { $reg[$k++] = $a[$y * $W + $x] } }
    ,@($a, $ink, $reg)
}
function ImgDiff($a, $b) { $s = 0.0; for ($i = 0; $i -lt $a.Length; $i++) { $s += [Math]::Abs($a[$i] - $b[$i]) }; $s / $a.Length }
function Shot($name) { $full.Save("$out\$name.png", [System.Drawing.Imaging.ImageFormat]::Png) }
# waits until the screen stops changing for $quiet ms; returns ms from $t0 to the last change
function Settle([double]$t0, [int]$quiet = 8000, [int]$max = 40000) {
    $last = $sw.Elapsed.TotalMilliseconds; $lastChange = $last; $firstInk = $null
    while (($sw.Elapsed.TotalMilliseconds - $t0) -lt $max) {
        $r = Snap; $cur = $r[0]
        if ($script:prev -ne $null) { $d = ImgDiff $cur $script:prev; if ($d -gt 0.4) { $lastChange = $sw.Elapsed.TotalMilliseconds } }
        $script:prev = $cur
        if (($sw.Elapsed.TotalMilliseconds - $lastChange) -gt $quiet) { break }
        Start-Sleep -Milliseconds 10
    }
    [Math]::Round($lastChange - $t0)
}
$p = Start-Process $Exe -ArgumentList "`"$f`"" -PassThru
$t0 = $sw.Elapsed.TotalMilliseconds
$titleT = $null
$prev = (Snap)[0]
# phase 1: open. title time = window shows the file name; first draw = document area differs from its state at title time
$baseInk = (Snap)[1]; $baseReg = $null; $firstDraw = $null
$deadline = $t0 + 60000; $lastChange = $t0
while ($sw.Elapsed.TotalMilliseconds -lt $deadline) {
    if ($ProcName) { $procs = @(Get-Process -Name $ProcName -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 }) } else { $procs = @(Get-Process | Where-Object { $_.MainWindowTitle -match $TitleMatch }) }
    if ($procs.Count -gt 0 -and $titleT -eq $null) { $titleT = [Math]::Round($sw.Elapsed.TotalMilliseconds - $t0); $tp = $procs[0]; [W]::MoveWindow($tp.MainWindowHandle, 100, 100, 1200, 700, $true) | Out-Null; [W]::ShowWindow($tp.MainWindowHandle, 3) | Out-Null; [W]::SetForegroundWindow($tp.MainWindowHandle) | Out-Null; Start-Sleep -Milliseconds 400 }
    $r = Snap; $d = ImgDiff $r[0] $prev; $prev = $r[0]
    if ($titleT -ne $null) {
        if ($baseReg -eq $null) { $baseReg = $r[2] }
        elseif ($firstDraw -eq $null -and (ImgDiff $r[2] $baseReg) -gt 4) { $firstDraw = [Math]::Round($sw.Elapsed.TotalMilliseconds - $t0) }
    }
    if ($d -gt 0.4) { $lastChange = $sw.Elapsed.TotalMilliseconds }
    if ($firstDraw -ne $null -and ($sw.Elapsed.TotalMilliseconds - $lastChange) -gt 2500) { break }
    Start-Sleep -Milliseconds 10
}
$openSettle = [Math]::Round($lastChange - $t0)
Shot 'open'
if ($tp) { $tp.Refresh(); [W]::ShowWindow($tp.MainWindowHandle, 3) | Out-Null; [W]::SetForegroundWindow($tp.MainWindowHandle) | Out-Null; Start-Sleep -Milliseconds 1500 }
$sh = New-Object -ComObject WScript.Shell
if ($tp) { [void]$sh.AppActivate($tp.Id) }
Start-Sleep 1
$prev = (Snap)[0]
$res = [ordered]@{ tag = $Tag; title_ms = $titleT; doc_area_first_draw_ms = $firstDraw; open_settle_ms = $openSettle }
if ($Continuous) { [void][W]::SetCursorPos(294, 68); Start-Sleep -Milliseconds 300; [W]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); [W]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Seconds 3; Shot 'continuous' }
if ($PreEndWait -gt 0) { Start-Sleep $PreEndWait; $prev = (Snap)[0] }
$t = $sw.Elapsed.TotalMilliseconds; [System.Windows.Forms.SendKeys]::SendWait('{END}'); $res.end_settle_ms = Settle $t; Shot 'end'
$t = $sw.Elapsed.TotalMilliseconds; [System.Windows.Forms.SendKeys]::SendWait('{HOME}'); $res.home_settle_ms = Settle $t; Shot 'home'
# 10 x PageDown, 400 ms apart, then settle after the last key
$t = $sw.Elapsed.TotalMilliseconds
for ($i = 0; $i -lt $Scroll; $i++) { [System.Windows.Forms.SendKeys]::SendWait('{PGDN}'); Start-Sleep -Milliseconds 400 }
$tl = $sw.Elapsed.TotalMilliseconds; $res.pgdn_settle_after_last_ms = (Settle $tl)
Shot 'scroll'
# zoom: Ctrl + wheel x6 at the centre of the document area, then fast wheel scroll x40
[void][W]::SetCursorPos([int]($scr.Width * 0.55), [int]($scr.Height * 0.5)); Start-Sleep -Milliseconds 300
$prev = (Snap)[0]
$t = $sw.Elapsed.TotalMilliseconds
[W]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
for ($i = 0; $i -lt 6; $i++) { [W]::mouse_event(0x0800, 0, 0, 120, [UIntPtr]::Zero); Start-Sleep -Milliseconds 120 }
[W]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
$res.zoom_in_settle_ms = Settle $t 3000; Shot 'zoom'
$t = $sw.Elapsed.TotalMilliseconds
[W]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
for ($i = 0; $i -lt 6; $i++) { [W]::mouse_event(0x0800, 0, 0, -120, [UIntPtr]::Zero); Start-Sleep -Milliseconds 120 }
[W]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
$res.zoom_out_settle_ms = Settle $t 3000
$prev = (Snap)[0]
for ($i = 0; $i -lt 40; $i++) { [W]::mouse_event(0x0800, 0, 0, -120, [UIntPtr]::Zero); Start-Sleep -Milliseconds 30 }
$tl = $sw.Elapsed.TotalMilliseconds; $res.wheel40_settle_after_last_ms = Settle $tl 3000; Shot 'wheel'
$pn2 = if ($ProcName) { $ProcName } else { 'FoxitPhantomPDF' }
$mp = @(Get-Process -Name $pn2 -ErrorAction SilentlyContinue)
$res.ram_private_mb = [math]::Round((($mp | Measure-Object PrivateMemorySize64 -Sum).Sum) / 1MB)
$pw = 0; foreach ($q in $mp) { $c = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($q.Id)" -ErrorAction SilentlyContinue; if ($c) { $pw += $c.WorkingSetPrivate } }
$res.ram_private_ws_mb = [math]::Round($pw / 1MB)
$res.ram_workingset_mb = [math]::Round((($mp | Measure-Object WorkingSet64 -Sum).Sum) / 1MB)
if ($IdleWait -gt 0) {
    Start-Sleep $IdleWait
    $mp2 = @(Get-Process -Name $pn2 -ErrorAction SilentlyContinue)
    $pw2 = 0; foreach ($q in $mp2) { $c = Get-CimInstance Win32_PerfFormattedData_PerfProc_Process -Filter "IDProcess=$($q.Id)" -ErrorAction SilentlyContinue; if ($c) { $pw2 += $c.WorkingSetPrivate } }
    $res.idle_private_mb = [math]::Round((($mp2 | Measure-Object PrivateMemorySize64 -Sum).Sum) / 1MB)
    $res.idle_private_ws_mb = [math]::Round($pw2 / 1MB)
}
$res | Format-List | Out-String
$p.CloseMainWindow() | Out-Null; Start-Sleep 3
if ($ProcName) { Get-Process -Name $ProcName -ErrorAction SilentlyContinue | Stop-Process -Force } else { Get-Process | Where-Object { $_.MainWindowTitle -match $TitleMatch -or $_.Id -eq $p.Id } | Stop-Process -Force -ErrorAction SilentlyContinue }
