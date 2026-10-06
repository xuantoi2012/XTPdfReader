# Foxit / Reader runtime observation — 2026-10-05

## Capture

User-authorized desktop observation using Computer Use, passive process counters,
per-thread CPU counters, and a separate sampled x86 Foxit UI-thread stack pass.
Reader was built from `757888b` in Release before the capture. Comparator was the
installed Foxit PhantomPDF 10.0.0.35798. No source PDF was edited or saved.

Input: Desktop `03. QUYEN 2.2 - TNM, TNT, HKT, CAY XANH, CHIEU SANG, TCTC.pdf`,
173,799,414 bytes, **284 pages**. This is not the identical 276-page input from
the older reports. Page 31 was selected directly and visually verified as
“TRẮC DỌC HỆ THỐNG THOÁT NƯỚC MƯA TRÁI TUYẾN”.

Both windows were maximized on the same screen with sidebar panels collapsed.
Foxit 69.3% / Reader 100% showed approximately 1100 px paper width. Deep view:
Foxit 208% / Reader 300%. Reader was dragged into the same crop: the paper left
edge was x≈647 and inner border x≈805; major linework aligned within a few pixels.
The usable vertical viewports still differ slightly. Both used the hand tool,
dragging from (1450,660) to (1150,660), then back, with snapshots between actions.

The first Foxit preparation process is **excluded**: Ctrl+A accidentally selected
document text when the zoom field had not taken focus. Foxit was closed normally
and relaunched. Numeric zoom entry used physical digit keys through Ctrl+M;
Unicode text entry was rejected by this Foxit edit control.

Computer Use activation restored window sizes; each measured pan followed explicit
maximization and visual verification. Zoom changes made during preparation/resizing
are not matched latency measurements. Screenshot completion is not display latency.

## Observations

Resource windows span approximately 0.2 s before to 2 s after each pan marker.
These are **single exploratory samples**, including observation overhead and any
background activity. CPU is accumulated process CPU time, not elapsed render time.

| Action | Foxit private MiB | Reader private MiB | Foxit CPU ms | Reader CPU ms |
|---|---:|---:|---:|---:|
| Pan left 300 px | 123.0 | 696.6 | 1078.1 | 671.9 |
| Pan right, revisit | 123.0 | 696.6 | 984.4 | 671.9 |

Both snapshots contained sharp, aligned drawing content after each pan. There is
no frame sequence here to establish absence of intermediate blur, blank frames,
or stroke-weight changes. No FPS or engine speedup claim follows from this table.
Reader had already warmed its document; Foxit was a fresh process with a different
history of background work. The memory difference is an investigation target,
not an equal-retention engine benchmark.

Thread counters in these windows identified Foxit TID 10392 as the main CPU user
(about 750–922 ms), with TID 10108 at about 156 ms. Reader work was concentrated
on TIDs 23200 and 41324 (about 250–391 ms each). This does not identify private
rendering algorithms or justify claiming that Foxit uses only one thread globally.

Reader Debug snapshot near 20:17 Bangkok time:

* Private 688 MB, managed heap 42 MB; reader images 65 MB, regions 35 MB,
  thumbnails 88 MB / **284 images**, residual estimate 457 MB.
* Native heap committed 625 MB, allocated 237 MB. Committed heap capacity is not
  synonymous with live PDFium allocations; the residual also includes WPF/driver work.
* 734 page loads, 3 native page-cache hits; 729 parsed pages on the background
  PDFium instance, 5 on the primary instance. This is session-wide, not just pan.
* Parse avg/max 42.1/112.5 ms; raster slice 8.2/229.6 ms; WPF copy 0.3/9.4 ms;
  gate wait 24.0/5657.0 ms. These do not include full WPF presentation timing.

The Reader's whole-document preview warmer is an important source of work to
isolate in a future on/off comparison. A preliminary impression that it remains
continuously busy while covered was **not confirmed**: from 13:13:55 to 13:14:25
UTC it used only 0.16 CPU seconds and read no further process-I/O bytes. Foxit
used 6.70 CPU seconds and 2.93 MiB process reads in that same observation window.
Neither value is an intrinsic idle cost; initialization and tool activity differ.

## Stack/ETW limits

WPR GeneralProfile failed with `0xc5585011` (“Failed to enable the policy to
profile system performance”). No ETW GPU, disk-path, or input/presentation trace
was obtained. ProcMon was not available on PATH and was not installed in this pass.

A separate read-only memory/context sampler briefly suspended/resumed Foxit's
UI thread, sampled EIP and candidate stack return addresses, and collected 16,780
samples over 60 s. It injected no input; all desktop actions used Computer Use.
Sampling itself can perturb timing, so this pass is excluded from the resource table.
Most samples were waiting in win32u/ntdll. COM, UIAutomationCore, OLEACC and mshtml
also appeared: the observation helper introduces work even without requesting PDF
accessibility text. There were too few direct executable samples during the two
drags to attribute raster routines reliably. Candidate stack addresses are not
symbolized call stacks. This pass does **not** establish a tile size, bitmap format,
disk-cache policy, or GPU raster path for Foxit.

## Concrete fixes and direction

Inspection found that `ContinuousPdfView.CancelAll()` cancelled visible page work
but left the whole-document preview-warming CTS active. `SetDocument()` also kept
the old document's `_warmed` entries, so switching to another document with the
same page count could incorrectly suppress its preview warmup.

* Cancel preview warming with the viewer's other work on rebind, suspension,
  unload and shutdown; reset completion bookkeeping when binding a document.
* Display the actual thumbnail-cache budget (160 MiB), replacing the stale
  hard-coded 48 MiB Debug label. The 88 MiB reading was not a budget violation.
* Add a behavioral regression that warms the far page of two consecutive
  20-page documents, outside the visible/nearby prefetch range.

Validation: Release app build succeeded; test build has the pre-existing CS8602
warning in `Tests/UiSmokeTests.cs:117`. Focused warm lifecycle test passed 2 checks;
background regression passed **491 checks**. The fixes have not been assigned a
RAM, latency, or FPS improvement from the pre-fix desktop measurements.

Current code also differs from older OPEN-IO notes: progressive presentation is
enabled by default, zoom rate is 2.6 ln/s, and legacy present timeout is 600 ms.
Do not use the earlier held-frame/2500 ms findings as a description of this build.

Next renderer experiment should isolate warm-preview work and native page retention,
then collect an uninstrumented frame sequence with the same content-rich anchor.
Preserve cached visible content, prioritize newly exposed pixels, and cancel stale
jobs. Keep the existing WPF/PDFium renderer while testing those contracts; this
runtime pass provides no evidence supporting a GPU migration or additional replicas.

## Local artifacts

Ignored `Tests/bin/foxit-runtime-20261005/` contains `resources.csv`, `threads.csv`,
UTC action markers, `pan-summary.json`, `reader-debug.txt`, and separate Foxit stack
samples. Resource collector ended after 900 s; the thread collector was stopped
with its stop file. Reader's measured session was closed normally before rebuilding.

Passive collectors: `Tests/Record-DesktopResources.ps1` and
`Tests/Record-ViewerThreads.ps1`. Focused regression:

```powershell
dotnet build Tests/PerformanceTests.csproj -c Release
dotnet Tests/bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.PerformanceTests.dll --preview-warm-lifecycle
dotnet Tests/bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.PerformanceTests.dll --background-regression
```
