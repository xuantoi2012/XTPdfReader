# PDFium performance regression checks

Run on Windows with .NET 10 SDK from the app directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/Run.ps1
```

The runner obtains the original renderer from commit
`c08f4e2808929517cf6dc27066f6c0dcab0bbd14`, then runs baseline and optimized
renderers in separate processes. Pass `-BaselineRef` for another compatible
revision. Generated fixtures, hashes and JSON stay under Tests/bin/.../results.
No production PDFs or user settings are changed.

## Checks

The optimized suite passes 106 checks, including byte-budgeted LRU eviction,
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

- Reader/thumbnail/tile cache budgets: 160/48/48 MiB. Placements hold weak bitmap
  references, while visible WPF images and caches own strong references. These
  budgets are not total RAM limits. Active and retained fallback imagery plus
  native PDFium data and in-flight buffers use additional memory.
- Initial opening adds rows in bulk and warms four thumbnails. Expensive reader
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

## References

This adapts progressive scheduling and retained imagery to WPF, not Chromium's
entire compositor. Timing/cache constants are app choices, not Chromium constants.

- [PDFium progressive API](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/fpdf_progressive.h)
- [Chromium PDFium engine](https://chromium.googlesource.com/chromium/src/+/main/pdf/pdfium/pdfium_engine.cc)
- [WPF bitmap scaling modes](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.bitmapscalingmode)
