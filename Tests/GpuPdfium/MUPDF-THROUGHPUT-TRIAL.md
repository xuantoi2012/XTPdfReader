# High-memory throughput trial (2026-10-06)

Executable: `bin/MuPdfThroughputTrial/XTPdfMergeApp.exe` with its local ignored
`mupdf-trial.json`. Close the existing reader manually before starting it.
No existing reader window was automated or closed during verification.

## Policy

- Normal unmodified-layer reader rendering uses MuPDF for all documents/pages,
  including thumbnails. Rendering errors do not silently fall back to PDFium
  in this mode; failed worker processes can restart on subsequent requests.
- PDFium remains a dependency for metadata and other paths, including layer
  overrides and annotation-inclusive rendering. This is not complete removal
  of the library, and not a packaged or licensed engine distribution.
- Four lazy persistent processes: two foreground page-affinity lanes and two
  background/thumbnail lanes. Separate processes do not share native contexts.
  Background native renders cannot block the foreground processes, but an
  already-running foreground render cannot be preempted.
- Requests of at least 1024 px prepare a whole-page image of at least 4608 px.
  The reader reuses that image through nearby zoom levels. Exact-size rendering
  and waiting for a matching frame are disabled in this opt-in mode.
  Deeper zoom beyond the prepared resolution still needs a new crop; this is
  not a guarantee of infinite sharp zoom or imperceptible resampling.
- Frozen WPF bitmap cache: 2 GiB; reader page cache: 2 GiB; region cache: 1 GiB.
  These can contain references to the same bitmap, so their budgets are not
  additive measures of physical memory. Python has a 512 MiB raster LRU and
  64 display lists per process, plus native resource/document memory.
- Workers remain available for 30 minutes of idle time rather than 60 seconds.
  Shutdown clears the WPF bridge cache and stops every process.
- Existing nearby-page prefetch now populates larger reusable whole-page images.
  It does not eagerly prepare all 86 drainage pages at every zoom level.

## Measurements

Background WPF service calls; OS caches not flushed; no live FPS measurement:

| Page | First full-page request, 4608 px |
| --- | ---: |
| Drainage 1 | 1303 ms |
| Drainage 2 | 623 ms |
| BinhDo 1 | 984 ms |
| BinhDo 2 | 829 ms |
| BinhDo 3 | 1564 ms |
| BinhDo 4 | 757 ms |
| Drainage 1 cached revisit | 22 ms |

Four workers reached about 902 MiB private commit after thumbnail preparation;
this excludes WPF, PDFium and other parent-process allocations. Cache hits
still stat the file (which can be on a network drive) to detect source changes.
The initial image takes longer than the earlier 1200-pixel trial, in exchange
for a stable higher-resolution image after it completes.

A later run completed four uncached 1201x801 crops (two foreground, two
background requests on independent lanes) in 242 ms as a batch; worker private
commit then reached 985 MiB. This is not a serial-versus-parallel speedup claim.
Verification: 32 real MuPDF checks, four offscreen WPF policy checks,
29 legacy viewport/recovery checks and 159 scroll-quality checks passed.

Tests cover real multi-page render output, frozen bitmap identity reuse across
nearby requested widths, four worker processes, uncached concurrent requests,
thumbnail routing without PDFium page loads, cancellation on cache hits,
clip rounding and offscreen WPF zooms without new page/crop requests.
