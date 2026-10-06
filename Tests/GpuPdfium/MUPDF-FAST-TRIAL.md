# Multi-page fast trial (2026-10-06)

Local executable: `bin/MuPdfFastTrial/XTPdfMergeApp.exe`.
Its ignored `mupdf-trial.json` enables all pages of the supplied drainage and
BinhDo PDFs only. Other files, layer overrides, annotation-inclusive rendering,
and thumbnails retain PDFium. This is not a GPU implementation.

The persistent Python MuPDF worker retains 12 page display lists and up to
512 MiB of RGB raster results. Document identity includes path, modification
time and size. Changed sources invalidate retained results. Visible work has
priority over queued background work; an already-running native render cannot
be preempted. Full-page previews and background page renders now use this route,
not only the zoomed crop on page one. Pixel clipping is normalized to the exact
requested origin and dimensions to prevent rounding from rejecting a reply.

Measured through the actual WPF service, 1200-pixel full-page requests:

| File/page | First request ms |
| --- | ---: |
| Drainage 1 | 1014 |
| Drainage 2 | 224 |
| BinhDo 1 | 519 |
| BinhDo 2 | 363 |
| BinhDo 3 | 819 |
| BinhDo 4 | 311 |
| Drainage 1 revisit | 10 |

Worker private memory reached about 371 MiB in that sequence, excluding WPF and
PDFium memory. These are background timings, not an interactive FPS measurement
or a comparison with Foxit. OS file cache was not flushed. First-time parsing
and rasterization still cost time; cached results do not eliminate that cost.

Verification: 25 multi-page/crop checks, 29 viewport/cancellation/recovery checks,
159 scroll-quality checks, and Release build passed. The tests verify nonblank
images and that full-page requests avoid PDFium page loads. Cancellation uses
an uncached crop so it exercises real in-flight work instead of a cache hit.

Close the existing reader manually before launching this executable because
the app is single-instance. Visual sharpening smoothness still needs user
testing; this trial does not claim imperceptible image replacement.
