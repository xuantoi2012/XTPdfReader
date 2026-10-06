# Foxit runtime study - 2026-10-06

## Evidence collected

Foxit PhantomPDF 10 opened the supplied drainage PDF, restoring page 4/86 at
300%. The confirmed zoom sequence included 300 -> 400 -> 300 -> 400%.
This is a warm interaction probe of page 4, not a cold benchmark of page 1.
The keyboard-driven zoom steps are not a mouse-wheel latency measurement.
UI Automation text lagged behind screenshots; it cannot be used as a frame
timestamp. Screenshot snapshots cannot establish flicker duration or whether
Foxit replaces a whole-page surface, tiles, or individual drawing operations.

Two process-thread CPU accounting runs were saved under ignored
`Tests/bin/foxit-study/`. The 50-second run recorded 10,703 ms on thread 27352
and 1,578 ms on thread 11396. The 40-second run recorded 6,469 ms and 813 ms
respectively; a third thread recorded 16 ms. Those are aggregate CPU times,
not render durations. No call stacks were collected, so neither hot thread
can yet be identified as a renderer or the UI thread. Thread CPU deltas do
not prove simultaneous execution inside a sampling interval.

WPR CPU capture failed with `0xc5585011` (failed to enable the policy to
profile system performance). The current token has no profiling privilege.
Thus no ETL, GPU trace, Ghidra hotspot mapping, or confirmed Foxit cache
algorithm resulted from this session. The process had 31 threads and about
397 MiB private memory at one snapshot; thread count is not worker count.

## Confirmed bottleneck in XTPdfReader

`Services/PdfThumbnailService.cs`, `RenderPageTilesStreamingAsync`, selects
one PDFium instance for the entire batch. In
`Services/PdfThumbnailService.Progressive.cs`, `RenderTilesProgressiveAsync`
holds the page operation gate and awaits each rectangle in a sequential loop.
Increasing the pool to four instances does not parallelize this batch.

`Controls/ContinuousPdfView.cs`, `LoadRegionAsync`, supplies a no-op tile
callback and waits for all tiles before composing the replacement crop.
This avoids visible tile assembly, but makes sharp-frame latency depend on
the slowest complete batch. Live zoom also defers fresh raster requests for
24 ms after the most recent zoom timestamp. Cache scaling still responds,
but sharp raster updates can wait through a continuous gesture.

These are code-level facts, not inferred Foxit behavior. The 2026-10-05
prototype already showed that direct GPU raster was slower than CPU on the
heaviest supplied CAD page; see PROVIDED-FILES-2026-10-05.md.

## Reference implementation inspected

SumatraPDF's current RenderCache.cpp uses a shared request queue with lazily
created workers. GetNextRequest takes the newest queued request. It aborts
obsolete same-tile jobs at different zooms and checks aborted results before
cache admission. PaintTile can use a cached bitmap from another zoom while
requesting the target zoom. Paint walks a tile-resolution hierarchy and skips
offscreen intersections. These mechanisms are verified from public source;
they do not establish what Foxit does. The source is GPLv3; this study records
architecture observations and does not incorporate its code into our app.

Source inspected on 2026-10-06:
https://github.com/sumatrapdfreader/sumatrapdf/blob/master/src/RenderCache.cpp

## Next decisive capture

Run Capture-FoxitStacks.ps1 in an elevated PowerShell session. It records a
bounded CPU ETL while the user performs the zoom sequence. Open the ETL in
WPA, filter to FoxitPhantomPDF.exe, and inspect CPU Usage (Sampled) by Thread
and Stack plus CPU Usage (Precise), if included by the selected profile.
Public Windows symbols resolve system frames; Foxit private frames may remain
module + offset. Map only the dominant engine offsets into a disassembler.
Without symbols, identify functions by evidence from API calls and repeated
stacks, rather than assigning guessed names.

Compare first/revisited zoom CPU work, overlapping active engine stacks, and
UI-thread waits. This can establish worker activity and repeated computation;
bitmap/display-list cache identity still needs additional allocation or API
evidence. Frame latency requires synchronized presentation capture separately.

Microsoft CPU analysis reference:
https://learn.microsoft.com/en-us/windows-hardware/test/wpt/cpu-analysis

Recommended app experiment after tracing: distribute independent viewport
tiles across two warmed PDFium instances with bounded concurrency, then compare
against a single full viewport crop. Measure parse duplication, batch latency,
obsolete work, and native memory. Keep presentation policy separate from tile
execution so parallel rendering does not force visible per-tile updates.
