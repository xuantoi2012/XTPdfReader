# Open-time file I/O: lazy local buffer + small-read window, 2026-10-02

## What was measured

Same file (`03. QUYEN 2.2 ...pdf`, 165.7 MiB), same keystrokes (about 80 PageDown, End, Home, 60 s idle,
`SendKeys` in the foreground for both apps). Foxit PhantomPDF 10.0.0.35798 (x86) versus this Reader (Release).

| At open (first 10-28 s) | Foxit | Reader before | Reader after |
|---|---:|---:|---:|
| Bytes read | 6 MB | 176 MB | 69-70 MB |
| Read operations | ~2,900 | ~798,000 | **~3,180** |
| Bytes written (temp file) | - | 166 MB | 42-43 MB |
| Private memory | 84 MB | 216-225 MB | 187-191 MB |

Foxit's own mechanics, from the same capture and from RTTI class names (no code was copied):

- Reads are lazy `ReadFile` ranges (about 2.5 KB average, ~47k operations over the whole scroll), not a mapping of
  the file: its `MEM_MAPPED` stayed constant at 52 MB. Its `CFX_MMapedFile` is a write buffer.
- Its private memory is **not** ~200 MB under a real scroll: 84 MB at open, 643 MB after the scroll, and it did not
  release anything during 60 s idle. The earlier 200 MB figure came from a lighter workflow.
- Reader after idle drops to ~540-560 MB, i.e. already below Foxit's 615 MB.

## Causes found in this Reader

1. `PdfBlockCache` copied the whole file to a temp file in the background (176 MB read, 166 MB written per open).
2. About 800,000 read operations at open, average ~65 bytes: PDFium and iText (`BlockCacheSource.Get` reads one byte
   at a time) each issued one `RandomAccess.Read` system call per small read.

## Changes (`Services/PdfFileBuffer.cs`)

- **Lazy local mode**: fixed local drives no longer start the background copy. Blocks enter the temp file when PDFium
  or iText reads them (plus the existing read-ahead). The temp file is marked sparse so a far jump does not make NTFS
  zero-fill everything before it. `LoadedFraction` reports 1 so the "Loading into memory" text and thumbnail warm-up
  do not wait forever. `XTPDF_LAZY_LOCAL=0` restores the old background copy (network drives always use it).
- **Per-thread 64 KB read window** in `PdfBlockCache.CopyTo` for reads up to 4 KB. The window stays inside one
  already-present block (present blocks never change), so it cannot return unloaded sparse data.

Peak private memory during scroll (~890 MB) and the scroll-time behaviour are unchanged by this work.

## Verification

Release build 0 warnings/errors; `--background-regression` PASS (391 checks). The first launch of a freshly built exe
can be slow (one run sat idle for about a minute); measure from the second launch.

## Not done / next

- Scroll peak (~890 MB versus Foxit 643 MB) and the 187 MB empty baseline versus Foxit 84 MB.
- Render latency was measured afterwards (next section); PageDown distances still differ between the two apps.
- A scripted check that lazy and old modes render identical page hashes was not added.

## Render latency and deep zoom (GUI, same PDF, both apps in the foreground)

`Tests/Measure-GuiLatency.ps1` sends the same keys and Ctrl+wheel to each app and captures the content area
continuously for 2.5 s after every action; the last frame is the finished image. "Ready" is the first moment the
frame equals the finished image (grey tolerance 12, 8 samples). This is on-screen readiness, not GPU FPS; both apps
were foreground, the machine was otherwise idle, one run each, so treat differences under ~30 ms as noise.
`Tests/Measure-GuiProcessIo.ps1` is the process I/O and memory sampler used above. Both take over mouse and keyboard
for minutes and must be closed gracefully (killing Reader makes the next launch show the recovery dialog).

| Median ready, ms | Foxit | Reader |
|---|---:|---:|
| New adjacent page (PageDown; Reader prefetches 4 pages) | 386 | 49 |
| Jump to a far, unseen page (End) | 152 | 168 |
| Return to a seen page | 50 / 118 | 41 / 32 |
| Zoom in, normal range | 36 | 28 |
| Zoom out | 50 | 18 |
| Pan while zoomed | 35 | 18 |
| Deep zoom in, 10 notches | ~350 | ~400 |
| Deep zoom out, 20 notches | 850 | 650-700 |
| **Return to a seen page after deep zoom** | **2,700-3,400** | **29-169** |
| Private memory peak in this run | 952 MB (deep zoom) | 507 MB |

Foxit stopped changing on several deep-zoom-in steps (it reached its own maximum), so those rows are not comparable
zoom-for-zoom. The far-page jump is the only real cold read and the two apps are level there.

## Zoom limit

`MaxZoom` was 400% and the sharp region was capped at a 16,000 px page width (about 1,450% on a 1,100 px page).
Both are raised: `MaxZoom` 32 (3,200%; Reader 100% = ~1,100 px across the paper, so this is roughly Foxit's 2,200%)
and `MaxRegionFullWidth` 65,536. Regions only rasterize the visible viewport, so memory does not grow with zoom.
The zoom slider is now on a square-root scale so it stays usable at low zoom. Checked visually at 1,166%: vector text
edges are crisp. Not checked: 3,200% itself, other page sizes (A0 layouts exceed the 65,536 px cap earlier and then
upscale), and rotated pages at extreme zoom.

## Zoom "pixel jump" between two sharpening passes

Reported by the user: while zooming, Reader visibly changes pixels between two renders, Foxit does not.

`Tests/Measure-GuiLatency.ps1 -Mode zoom` now compares full-resolution pixels (640x420 crop, no sub-sampling; the first
version sampled every 6th pixel and missed thin CAD lines entirely) on a drawing page (page 31), 14 Ctrl+wheel zoom-in
and 14 zoom-out steps. "Jump" = pixels that still differ between the first changed frame and the finished image.

Cause found in `ContinuousPdfView.UpdateRequests`: after each zoom step the old image was only scaled for 60 ms
(`ZoomSettleMilliseconds`), then a new page image was requested whenever the 256 px size bucket changed, even though the
image on screen was still large enough. Each replacement is a fresh raster whose thin lines differ from the old image
shrunk by WPF, so it reads as a jump (27,000-50,000 pixels, ~170 ms) every 2-4 notches. First attempt (render 25%
larger) did not help for that reason: it still replaced the image at every bucket change.

Fix: hysteresis. A page image is only re-rendered when it is about to fall below 1.1x the needed width, and then 2x
larger (`PageLowWater`, `PageRefreshHeadroom`; first render stays 1.25x so a new page is not slower). A sharp region is
reused while it is 1.06-3x the needed width and covers the visible part, compared in page fractions
(`RegionLowWater`/`RegionHighWater`); new regions are rendered 1.6x larger. `ZoomSettleMilliseconds` is 24 ms.

| 14 zoom-in steps, drawing page | Before | After | Foxit |
|---|---:|---:|---:|
| Steps where the image jumps | 4 (every ~3 notches) | 2 | 3 |
| Duration of those jumps | ~180 ms | ~150-170 ms | 30-115 ms |
| Zoom-out steps with a jump | 0 | 0 | 4 |
| p90 ready time, all 28 steps | 178 ms | 34 ms | 1,765 ms (first steps are slow) |

New page / revisit / pan latency is unchanged (three repeat runs: PageDown 31-60 ms, far jump 170-179 ms, one 446 ms
outlier, revisit 31-53 ms). Memory in the same runs was 371-515 MB private. Regression suite passes (391 checks) after
four tests that hard-coded the old width formula were updated (expected widths, the region helper, one fixture
threshold, and a "fresh crop" area check relaxed from >= 100% to >= 90% of the original; a reused band is under 60%).

Not solved: two zoom-in steps in 14 still replace the image once, and each lasts longer than Foxit's. Next idea if it
is still visible: cross-fade the old and new image over ~80 ms instead of swapping.

## Cross-fade for image swaps

`ContinuousPdfView.CrossFadeMilliseconds` (100 by default, `XTPDF_CROSSFADE_MS=0` turns it off): when a sharper page
image replaces a real page image (previous width >= 820 px, so thumbnails do not slow a newly opened page), or a sharp
region appears over existing content, the old image stays underneath and the new one fades in with a smoothstep ramp.
Frames are requested only while a fade runs. New unit test `TestCrossFade` (blended frames exist with 100 ms, none with
0 ms, both end on the new image); regression suite 393 checks.

GUI check (`-Mode zoom`, `max_step` = largest change between two consecutive frames after the first 70 ms, two rounds
per setting, same build, only the environment variable differs):

| Swap step | Fade off | Fade 100 ms | Time to finish |
|---|---:|---:|---:|
| zin-12 (page image replaced) | 29,700 px | 11,300-12,800 px | 151-181 -> 218-231 ms |
| zin-3 (50,015 px both ways) | 50,015 | 50,015 | unchanged |

So an in-place replacement now changes by ~60% less per frame, at the cost of finishing ~70 ms later. The zin-3 event is
not reduced; it is probably content appearing for the first time (instant by design) but that was not verified.
The earlier `jump_samples` column and the unfiltered `max_step` are dominated by the zoom motion itself and should not be
used to judge the fade.

## Exact raster and present-when-ready (what the user actually perceives)

User report: while zooming, Reader changes the picture from soft to sharp and the change is noticeable; in Foxit it is not.
Frame extraction from the user's own screen recordings (29 fps) showed it is not blur: **stroke weight changes**. PDFium draws
hairlines at least one pixel wide *at the bitmap's resolution*, so a small bitmap scaled up has thick dark strokes and an
oversampled one scaled down has thin pale ones; replacing one by the other at a different raster size reads as "the picture
changed" even though both are sharp.

Foxit renders at the exact displayed size and shows the result only when it is finished (measured: on its slow first zoom steps it
keeps the old picture for ~2 s and then shows the new one in a single update; on faster steps one update per step, ~50 ms).

Implemented in `ContinuousPdfView` (`XTPDF_EXACT_RASTER=0` restores the previous policy; `XTPDF_PRESENT_TIMEOUT_MS`, default 250, 0 disables
holding the frame):

- Page and region images are rendered at exactly the displayed size and drawn 1:1 on the pixel grid. A page wider than ~1.1x the viewport
  uses the viewport region path (renders only the visible part) instead of a full-page bitmap up to 2,304 px.
- Wheel zoom (`ZoomAtWhenReady`) changes the logical view at once but keeps the picture on screen unchanged until the exact images of one
  zoom are ready, then presents exactly that zoom; if more notches arrived meanwhile the next step is rendered next (in-flight renders are
  not cancelled). A step slower than the timeout is presented with the old scaled image and cross-fade, so heavy pages never freeze.
- Scrolling or jumping flushes the held frame; viewport resizes (the horizontal scrollbar appearing) only update its size.
- Found on the way: a supplier may return an image of another size than requested (the page cache returns a larger cached one), which
  made exact mode re-request forever; `PageState.DeliveredWidth` remembers that the size was already requested.

Results on the 165 MB drawing set, page 31 (`Tests/Measure-GuiLatency.ps1 -Mode zoom`, 14 zoom-in and 14 zoom-out notches):

| | Foxit | Previous Reader | Exact + present (this change) |
|---|---:|---:|---:|
| Steps where the image visibly jumps | 7/28 | 2/28 | **0/28** |
| Zoom-in, time to the new picture (median) | 56 ms | 18 ms | 117 ms |
| Zoom-out, time to the new picture (median) | 60 ms | 18 ms | 48 ms |

Rapid rolling (`Tests/Capture-GuiZoomSequence.ps1 -Mode zoomroll`, 40 notches ~31 ms apart): Foxit 15 visible updates in 350 ms
(25 ms apart), previous Reader 17, this change 6 (median 33 ms but up to 166 ms apart). An earlier version of this change that held the
frame until the *latest* target was ready showed only 4 updates with a 280 ms freeze; stepping fixed part of it. The remaining gap is the
exact render itself: ~65-75 ms per 1376x781 region on this page, 100-150 ms per step end to end, against ~30 ms in Foxit.

Ideas not done: speculative pre-render of the next zoom step (hit rate drops once the wheel accelerates), a faster non-progressive
native path for visible regions, routing slider/button zoom through the same present logic (it would step at ~10 fps instead of
scaling at 30 fps, so it was left as is).
