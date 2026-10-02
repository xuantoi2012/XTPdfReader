# PDFium performance regression checks

Run on Windows with .NET 10 SDK from the app directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/Run.ps1
```

This always runs the current regression suite. To compare with a historical renderer that
exists in the current Git checkout, pass its commit explicitly:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/Run.ps1 -BaselineRef '<commit>'
```

When `-BaselineRef` is supplied, the runner obtains that version of the renderer
and runs baseline and optimized renderers in separate processes. Generated
fixtures, hashes and JSON stay under Tests/bin/.../results.
No production PDFs or user settings are changed.

## Checks

### Background Native Retention And Previews

```powershell
dotnet build Tests/PerformanceTests.csproj -c Release
./Tests/Run-ReaderRetentionComparison.ps1 -Rounds 3
```

This compares full-page-only caching, bounded previews, and focused idle-native
consolidation in separate offscreen non-activating processes. It verifies matching
sharp pixel hashes, cache eviction fallback, 25-second idle memory and resumed
zoom. See [the measured decision and limitations](FOXIT-RETENTION-2026-10-02.md).
Timings are render/cache readiness, not display FPS. Source PDFs stay unchanged.

### Background Heavy-File Profile

```powershell
dotnet run --project Tests/PerformanceTests.csproj -c Release -- --background-heavy-profile default 4
$env:XTPDF_PDFIUM_INSTANCES = '3'
dotnet run --project Tests/PerformanceTests.csproj -c Release --no-build -- --background-heavy-profile balanced 4
Remove-Item Env:XTPDF_PDFIUM_INSTANCES
```

This uses the two most recent accessible PDFs larger than 100 MiB, read-only.
The real continuous viewer runs in an offscreen, non-activating test window at
below-normal process priority. It does not send desktop input, change settings,
write recents, restore edits, or modify the source PDFs. Results under
`Tests/bin/.../results/heavy-background-*.json` include page-ready latency,
simulated scrolling, 100% zoom, process memory, and 25 seconds of idle reclamation.
Dispatcher timer delay and simulated scroll ticks are **not display FPS**.
The production defaults are unchanged by these commands.

For a repeated pool-size comparison:

```powershell
dotnet build Tests/PerformanceTests.csproj -c Release
./Tests/Run-HeavyPoolComparison.ps1 -Rounds 3
```

The runner freezes the two source paths, excludes one warm-up, rotates the order
of 2/3/4/6 instances over three rounds, and checks the actual loaded instance count.
Each run uses exactly 100 scroll steps (6,500 DIPs) per file, checks 100% zoom
pixel hashes across configurations, and tests a new page after 25 seconds idle.
Source buffering completes before the interaction clock starts. This does not
flush OS caches or claim a cold network read. Active dispatcher delays exclude
the long idle period. Individual runs and a median summary remain in `results`.

See [the repeated pool comparison](POOL-BALANCE-2026-10-02.md) for the final
two-instance default and measured tradeoffs. The earlier
[heavy-file comparison](HEAVY-FILES-2026-10-02.md) is preliminary.

To run regressions without foreground dialogs or recovery windows:

```powershell
dotnet run --project Tests/PerformanceTests.csproj -c Release --no-build -- --background-regression
```

This skips only those two foreground UI test groups. Continuous-view checks use
an offscreen, non-activating host; native render/save tests use generated fixtures.
`--pool-policy` runs just the default/override policy checks without showing windows.

### Reader Tuning

```powershell
./Tests/Run-ReaderTuningComparison.ps1 -Rounds 2
```

This screens prefetch, bitmap/native cache budgets, native retention and warm-file
limits with structured phase diagnostics, native parse reuse and GC pause counts.
`-Variants baseline,viewport-first -ZoomCycles 2 -DeepZoom` compares the selected
deep-zoom policy, including a pan beyond the retained crop margin. Source paths
can be frozen with `-SourceManifest`; every configuration must render matching
100%/300%/pan pixels. See [the reader tuning report](READER-TUNING-2026-10-02.md).

### Warm Zoom and Foxit Follow-Up

```powershell
dotnet run --project Tests/PerformanceTests.csproj -c Release --no-build -- --warm-cache-profile off false --profile-sources '<source-manifest.json>'
dotnet run --project Tests/PerformanceTests.csproj -c Release --no-build -- --warm-cache-profile on true --profile-sources '<source-manifest.json>'
```

This read-only, offscreen profile selects the last file in a frozen heavy-profile
manifest, repeats normal/deep zoom and document rebinds, records cache memory and
native work, then idles for 25 seconds. Run the modes in separate alternating
processes. Cached-image readiness is not display latency or FPS. Results and
desktop observations are in [the warm-cache report](FOXIT-ZOOM-CACHE-2026-10-02.md).

### Foxit Desktop Resource Comparison

See [the GUI/resource comparison](FOXIT-DESKTOP-COMPARISON-2026-10-02.md) for the
installed Foxit version, actual-pixel zoom matching, memory windows, native retention
diagnostics, and limitations. This is not a controlled FPS or engine-speed ranking.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/Summarize-DesktopResources.ps1 -CsvPath Tests/bin/desktop-comparison/resources-clean-20261002.csv -WindowsPath Tests/desktop-comparison-20261002-windows.json
```

The background suite passed 267 checks on 2026-10-02, including byte-budgeted LRU eviction,
reclaiming previously pinned tiles, weak bitmap ownership, WPF binding retention,
bulk page insertion, lazy progress, priority/cancellation/serialization, crop and
rotation geometry, same-request page/tile pixel equality against the original,
document retirement/reopening, native page reuse, progressive interruption,
two-buffer concurrency, frame queue budgets, pan/zoom scheduling and retained
image composition. A queue test cancels 499 obsolete visible requests before
admitting the destination. That is not an interactive 500-page benchmark.

## Measurement example: 2026-09-14

Release x64, warmed synthetic 12-page text/vector PDF, 12 full-page renders at
2200 pixels wide:

| Metric | Original | Optimized |
| --- | ---: | ---: |
| Managed allocation in render loop | 234,626,736 bytes | 143,928 bytes |
| Total render-loop time, one run | 1,054 ms | 1,022 ms |

Allocation excludes fixture generation and pixel hashing. Eliminating temporary
managed pixel arrays accounts for most of the difference. Native/WPF image memory
still exists: these numbers are **not process RAM**. Timing differences here do
not establish a general rendering speedup. Four pages and two tiles match the
original pixel for pixel. Adding 10,000 rows with one notification took 9.5 ms;
this excludes PDF loading, WPF layout and painting.

## Viewer architecture

- Sharp reader/recent-page preview/thumbnail/continuous-region cache budgets: 64/16/48/16 MiB. Placements hold weak bitmap
  references, while visible WPF images and caches own strong references. These
  budgets are not total RAM limits. Active and retained fallback imagery plus
  native PDFium data and in-flight buffers use additional memory.
- Recent-page previews are standalone downsampled pixels, not lazy transforms that
  retain full-resolution sources. They appear while sharp refinement runs and
  cannot satisfy a sharp-render request. Source/page/layer invalidation removes both tiers.
- After native idle, the hot PDF retains one document replica, prioritizing the
  explicitly focused page, then foreground hot handles. Active leases and queued
  operations are protected; bitmaps survive native retirement. The two-instance
  rendering pool remains available during interaction.
- Initial opening adds rows in bulk without warming thumbnails in every tab. Expensive reader
  rendering is requested for visible pages, not every virtualized container.
- Reader resolution follows viewport, zoom and DPI using quantized keys; larger
  zooms render visible tiles directly from geometry without requiring a full-page
  preview first. Page dimensions use PDFium's page-size API.
- PDFium calls remain serialized. Progressive rasterization releases the native
  gate between cooperative slices, with an 8 ms pause target. This cannot preempt
  arbitrary native parsing/decoding; this run observed a 31.5 ms maximum slice.
- Native page handles are reused with an idle cache limit of four; active pages
  can temporarily exceed it. At most two progressive bitmap buffers are allocated
  concurrently. Native internal memory is not byte-bounded by these counts.
- Pan refreshes coalesce at 16 ms; zoom changes debounce by 120 ms. Moving out of
  the previous page cancels obsolete page and tile work with identity-safe task
  cleanup. Same-page pan invalidates after accumulated movement of 35% of viewport
  size (minimum 64 DIPs); small movements preserve useful in-flight tiles.
- At a new resolution, incoming opaque tiles appear over the retained previous
  layer. Both coordinate spaces transform independently. The previous visual
  references are released once visible refinement completes. A WPF software
  composition test verifies new and old regions remain visible together.
- Visible tiles are ordered from the center. The former one-tile offscreen border
  was removed so outside pixels do not precede visible work. Presentation runs in
  WPF composition callbacks, up to four actions and a cooperative 4 ms budget.
- WPF resampling is LowQuality during interaction, HighQuality after 150 ms idle.
  Hardware composition depends on WPF rendering tier and the machine. PDFium
  rasterization is CPU-based. No WebView or custom GPU rasterizer is added.
- Merge/layer handling remains on the existing iText path.

## Stage diagnostics

The existing Debug PDF-loading panel reports session average/max milliseconds for
native document open, page load/parse, global gate wait, per-page queue, buffer
queue, native raster slices, WPF bitmap copies, UI queue and UI application.
Counters stay in memory. Set XTPDF_RENDER_TRACE=1 before launch only when detailed
logs are needed; default interaction avoids synchronous trace disk writes.

A raster slice is not a whole render. UI application timing is not GPU execution
or display latency. FPDF_LoadPage includes content parsing; decoding can also occur
inside rasterization. Use these stage values to locate delays, not as FPS claims.

## Tile versus viewport experiment

The warmed synthetic vector page's center crop at a 6400px page width, with
1920x1280 visible pixels, produced these medians of three measured passes:

| Maximum region edge | First region ready | Entire crop ready |
| --- | ---: | ---: |
| 640 px | 4.3 ms | 26.2 ms |
| 1280 px | 17.6 ms | 28.4 ms |
| 1920 px (one region) | 25.5 ms | 25.5 ms |

> **Superseded (2026-09-27, perf #4):** on dense CAD pages PDFium walks every object for each tile,
> so the reader now renders one viewport region per page (visible rect + 12.5% margin) instead of
> 640px tiles. See `PdfBench/BAO-CAO-SAU-TOI-UU-2026-09-27.md` (200%: 185 -> 75 ms).

640px tiles remain the implementation choice: this sample does not demonstrate a
meaningful total-time advantage for larger regions, and smaller regions appear
earlier. These are native output times, not interactive display latency.
Different clip origins caused small antialiasing differences (maximum channel
difference 4/255 in this fixture). The experiment records them instead of claiming
subdivisions are pixel-identical. Same-request comparisons still match exactly.

To measure a real PDF without changing it, after building the suite:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/MeasureViewport.ps1 -PdfPath 'C:\PDFs\drawing.pdf' -Page 500
```

This uses the selected page's center crop, prints stage counters and writes
Tests/bin/.../results/viewport-raster.json. It does not measure scrollbar layout,
thumbnail navigation, cold startup or GPU frame pacing. No real 500-page input
was available in this workspace for this change.

## Compare with MuPDF after cloning

Retain the optimized PDFium revision. Use the same local CAD/scan/mixed-size PDFs,
viewport, DPI, zoom and cache budgets. Separate cold launches from warm revisits.
Record first readable page, final sharp image, page/zoom delays, private bytes,
working set and memory after closing documents. Repeat and compare medians.
Inspect text, thin lines, crop, rotation and layers at matching effective resolution.
The automated suite does not certify interactive behavior on real CAD PDFs.

## Bounded spatial reuse (2026-10-02)

The current deep-zoom reader can reuse compatible viewport pixels and render
one missing band. It falls back to one full crop for fragmented/low overlap,
preserves existing cache budgets, and guards native clip-edge seams. This does
not reinstate the historical many-tile policy above.

See [REGION-REUSE-2026-10-02.md](REGION-REUSE-2026-10-02.md) for the repeated
heavy-file A/B results, quality tolerances and limitations.

After building `PerformanceTests.csproj`, run the generated test EXE:

```powershell
.\XTPdfMergeApp.PerformanceTests.exe --scroll-quality
.\XTPdfMergeApp.PerformanceTests.exe --background-regression
.\XTPdfMergeApp.PerformanceTests.exe --region-pan-profile off false 3 --profile-sources '<manifest.json>'
.\XTPdfMergeApp.PerformanceTests.exe --region-pan-profile on true 3 --profile-sources '<manifest.json>'
.\XTPdfMergeApp.PerformanceTests.exe --region-pan-profile quality true 3 --profile-sources '<manifest.json>' --profile-quality --profile-quick
```

Profile sources use the existing `RecentFile[]` JSON format and zero-based index.
Run A/B sequentially. Quality mode includes extra reference renders, so its
timing/memory numbers are not performance results. Quick mode skips idle trim.

## References

This adapts progressive scheduling and retained imagery to WPF, not Chromium's
entire compositor. Timing/cache constants are app choices, not Chromium constants.

- [PDFium progressive API](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/fpdf_progressive.h)
- [Chromium PDFium engine](https://chromium.googlesource.com/chromium/src/+/main/pdf/pdfium/pdfium_engine.cc)
- [WPF bitmap scaling modes](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapscalingmode)
