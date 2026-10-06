# Bounded memory during wide scrolling — 2026-10-06

Implemented in the local working tree and Release build. These policies now run
in the balanced MuPDF reader, not just the experiment harness.

## Runtime behavior

- Aggregate bitmap retention target: normal 512MiB, pressure up to 384MiB,
  critical 256MiB. Small machines can select lower targets. Displayed images can
  exceed these targets while pinned; these are not process-memory hard caps.
- Parent + child-worker private commit independently triggers pressure at
  1024MiB and critical response at 1536MiB, even on a machine with abundant RAM.
  Recovery requires private commit below 896MiB, healthy system headroom and
  45 sustained seconds; cache growth is gradual.
- Sample every second off the UI dispatcher. Foreground images remain protected;
  discard high-resolution reader/bridge/region cache entries outside the current
  viewport and its immediate neighbors. Bounded small previews remain available.
- Prefetch at most one sharp page ahead. Visible pages still render immediately;
  speculative prefetch waits 150ms after fast scrolling settles.
- Before rendering, reserve estimated output + native/IPC/WPF pixel buffers in a
  shared 192MiB weighted scheduler. Foreground waiters precede background work;
  cancellation and completion release reservations. An oversized request runs
  alone rather than deadlocking: this does not cap native document parsing.
- Limit normal native display-list retention to four per worker. Purge the MuPDF
  resource store on page transitions (50% normal, 100% under pressure). This
  PyMuPDF version reports `store_size()` as unavailable (`None`); no fabricated
  byte measurement or claimed native-store hard limit is used.
- Cold workers above 320MiB may be recycled between requests while holding their
  lane gate, unless their previous page still belongs to a protected viewport.
  No such recycle was required in the recorded final drawing run.
- Retire idle background lanes after 10 seconds under pressure, 30 seconds in
  normal mode. Foreground contexts remain alive for nearby zoom and pan.
- Eviction/finalization support: a noncompacting, nonblocking full collection
  after substantial dropped cache ownership, at idle or under pressure. Minimum
  interval 8 seconds normal / 3 seconds pressure; never call a blocking collection
  or finalizer wait from the UI. Released ownership is a heuristic, not an exact
  count of unique physical allocations shared by caches.
- Diagnostics expose pressure, budgets, collections, raster reservations and
  worker recycling. Explicit region invalidation also invalidates the bridge
  raster cache; generation checks prevent late replies from repopulating it.

## Drawing workload and results

Source: user's Desktop `03. QUYEN 2.2 - TNM, TNT, HKT, CAY XANH, CHIEU SANG,
TCTC.pdf`, 173,799,414 bytes, 284 pages. SHA-256 before/after each run matched.
Fresh sequential test processes, offscreen nonactivating WPF viewport 1320x700,
real native rendering, sharp 4608px page tier. No user window was closed or
activated. Measurements sum parent and worker **private commit**, not resident
physical RAM. The 50ms dispatcher sampler can miss shorter memory peaks.

Forward travel uses user scroll events at 120ms intervals with sharp checkpoints
every 20 pages. Reverse travel jumps ten pages at a time and waits for sharp
images. Then nearby zoom, deep zoom, pan and 12 seconds idle. Tests assert sharp
checkpoints, nearby image identity, viewport regions, idle pixel stability and
source integrity; they do not assert every moving frame is sharp.

| Run | Pages | Peak private MiB | After 12s idle MiB | Sharp-arrival median/max ms | Deep zoom ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Before changes, bridge/viewport baseline | 200 | 4770.4 | 3883.9 | 186.5 / 354.4 | 87.3 |
| First bounded implementation | 200 | 1212.5 | 1005.0 | 253.8 / 603.3 | 106.1 |
| Buffer-reuse experiment, rejected | 200 | 3410.4 | 3142.3 | 220.3 / 658.3 | 37.9 |
| Bounded implementation after tuning | 200 | 1353.0 | 916.9 | 282.0 / 674.7 | 66.0 |
| Shared production reader cache | 284 | 1293.5 | 859.3 | 243.5 / 659.6 | 110.0 |
| Final reader policy, idle background retirement | 284 | **1257.3** | **763.6** | **259.5 / 663.1** | **93.8** |

The matched 200-page viewport runs reduced sampled peak private commit by 71.6%
and idle private commit by 76.4%. Their distant-page sharp-arrival median rose
from 186.5 to 282.0ms: deliberately keeping less history costs cold revisits.
The final 284-page validation additionally exercises the exact shared cache
owned by ReaderWindow; it is a separate workload, not a matched baseline.
The final run kept two foreground workers, with a 174.6MiB estimated raster
reservation peak. Dispatcher maximum gaps were 173.5ms baseline, 172.7ms tuned,
172.5ms final; this is a coarse scheduling probe, not FPS or proof of no jank.

Buffer reuse was removed: in this workload it reduced allocation-driven GC
activity and increased retained private memory. Native heap compaction from the
earlier reclamation experiments also offered no useful improvement.

## Validation and reproduction

- Release build succeeds; existing CS8602 in `Tests/UiSmokeTests.cs:117` remains.
- 43 adaptive policy/reservation/cancellation/pinned-image checks passed.
- 86 MuPDF migration checks passed.
- 71 reader memory regression checks passed: render handoff/cancellation,
  preview retention, nearest-page prefetch, scrolling, warm image presentation,
  source invalidation, region reuse and rotation. Fixtures now request a deep
  zoom beyond the 4608px whole-page tier and accept a valid sharper predictive
  region while still checking coverage, version and layer identity.
- Four actual-PDF nearby-zoom viewer checks passed.
- Wide-scroll runs passed 39, 39, 40, 40, 53 and 53 checks respectively.

Run from repository root in PowerShell:

```powershell
dotnet build Tests/PerformanceTests.csproj -c Release
dotnet Tests/bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.PerformanceTests.dll --wide-scroll-memory-profile 'C:\Users\condu\Desktop\03. QUYEN 2.2 - TNM, TNT, HKT, CAY XANH, CHIEU SANG, TCTC.pdf' validation 284 --reader-cache
dotnet Tests/bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.PerformanceTests.dll --reader-memory-regression
```

Raw JSONs are in ignored
`Tests/bin/Release/net10.0-windows10.0.19041.0/results/wide-scroll-*.json`.
The final drawing run is `wide-scroll-reader-final-20261006-131200.json`.
This validates the reader viewport/backend and shared cache, not every main
window feature, multi-document editing, real OS memory pressure or a universal
RAM ceiling. No direct Chrome/Foxit side-by-side benchmark was performed.

## Chromium reference principles

The implementation follows viewport-priority scheduling, discarding stale work,
bounded reconstructible resources and sleeping inactive background contexts.
It does not claim to reproduce Chromium's allocator or PDF engine.

- [Chromium PDFiumEngine](https://chromium.googlesource.com/chromium/src/+/main/pdf/pdfium/pdfium_engine.cc): clear stale pending requests, cancel paints after scrolling, unload unused pages.
- [Chromium TileManager](https://raw.githubusercontent.com/chromium/chromium/main/cc/tiles/tile_manager.cc): soft/hard tile-resource budgets, priority queues, eviction before raster scheduling.
- [Chrome Memory Saver](https://support.google.com/chrome/answer/12929150?hl=en): deactivate inactive tabs and reload them on return.
