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

## Speculative zoom pre-render and region cost (04/10)

- Native cost of one visible region on a dense CAD page (1376 px wide): 8 px high 0.5 ms, 100 px 5.7 ms, 400 px 13 ms, 781 px 48 ms. LCD text, image-cache flags and the 8 ms slice make no difference; splitting into strips on one or two PDFium instances is slower (page re-parsed on the second instance).
- Speculation (render the +/- one zoom step in background after a wheel zoom, cancelled on scroll/jump): single-step zoom-in median 117 -> 28 ms, zoom-out 48 -> 26 ms (Foxit 56 ms), 0 visible jumps. Does not change rapid-roll cadence (6 updates per 350 ms vs Foxit 15).
- Deep zoom-out (20 notches) settle 2.4 s and revisit-after-deep 1.5 s are the same with XTPDF_SPECULATE=0, so they come from exact-size rendering of several pages, not from speculation. Earlier 697/169 ms figures were with bucketed widths.
- Fast-scroll blank frames rose to 13% with speculation running; speculation is now cancelled on scroll/navigate (not re-measured).

## Pan margin and deep zoom-out (04/10)

- Sharp region now leans toward the pan direction (lead margin 2.5x, trail 0.5x) and the next region is requested while panning as soon as the visible area is within a quarter view of the region edge. First version (half-view comfort) re-rendered continuously for 700 ms after each pan (settle 2.3-4 s); fixed by the quarter-view threshold. New `pan` mode in Measure-GuiLatency.ps1 (wheel pans, shift+wheel for horizontal). Wheel pans of 12-80 notches at 10-150 ms spacing show no post-pan sharpening tail on either the old or the new build, so this test cannot reproduce the effect the user sees; drag-pan / scrollbar-drag untested.
- Deep zoom-out (20 notches) settle 2.4 s and jump-to-END after it 1.5 s versus 0.7 s / 0.4 s on the pre-exact-raster build (3ed4d76): that build reused an oversized page bitmap scaled down (the stroke-weight 'jump'); exact raster re-renders each page at the displayed size. Same cost with speculation off.

## Wide background region for continuous pan (04/10)

- The user's case: zoom to ~100%, then pan left/right continuously at once; sharpening did not keep up. Wheel pans with the 12.5% region margin re-rendered a new region at every direction reversal.
- Fix: a second, independent region of up to 9 Mpx (visible area plus 100% margin on each side, only the missing strips are rendered) is rendered at Background priority in parallel with the on-demand region (`WideRegions`, `XTPDF_WIDE_REGION=0` disables). Zoom steps are unaffected (zin median 27 ms, zout 25 ms), peak private memory 558 -> 623 MB.
- Measured with `Capture-GuiZoomSequence.ps1 -Mode pan` (zoom with Ctrl+wheel, pan with Shift+wheel, direction flips every 8 notches) and `XTPDF_DEBUG_LOG` (`DRAW UNSHARP` = a frame in which the visible part of a region-mode page is not fully covered by a sharp region): 22-notch zoom 31 unsharp draws without the wide region, 0 with it; 26-notch zoom 10 -> 0.

## Pipelined zoom steps - tried, not kept (04/10)

Rendering the next zoom step (logical zoom at that moment) in parallel with the step being rendered, and presenting it next, did not raise the update cadence of a 40-notch Ctrl+wheel roll (updates in the first 350 ms, 2 runs each): zoom-in 4/5 without vs 2/3 with, zoom-out 10/10 without vs 10/10 with; max gap and settle time unchanged or worse. The in-flight and ahead renders compete for the same PDFium work and the presented step lags the logical zoom. Reverted.

## Cheaper steps: render flags, tight region, Foxit device class (04/10)

- Foxit binary (Ghidra dumps in C:\Users\condu\tools): the render device class is `CFX_AggDeviceDriver` (AGG, the same anti-aliased rasteriser family PDFium uses) and the result is blitted with `StretchDIBits`; no sign of a lighter rendering path.
- PDFium flags NO_SMOOTHPATH / NO_SMOOTHIMAGE / NO_SMOOTHTEXT on a 1376x781 region of page 21: 40.6 ms -> 36.6-37.7 ms (about 8%), nothing worth the change in look. Cost is geometry/parsing, not smoothing.
- Tight region (no margin) for the first 500 ms after a zoom step, with or without the wide background region: cadence of a 40-notch roll unchanged (zoom-in 5 updates, zoom-out 10 in 350 ms; two runs per configuration). Reverted.
- Why zoom-in shows only ~5 updates: with the faster wheel acceleration the zoom reaches the 32x limit about 300 ms into the roll, so there are simply fewer steps to show (the metric counts steps, not render speed). Max gap between updates stays 130-170 ms.

## Idle preview warming (04/10)

- Cause of the slow jump after a deep zoom (END 1.4-1.7 s): the ~10 pages visible at the destination were cold and each needed its 340 px preview rendered (page parse + render 0.4-1.2 s, two PDFium instances).
- Fix: when idle for 800 ms (no scroll/zoom/pan), the view renders the previews of the whole document into the shared ThumbnailCache at lowest priority, nearest pages to the current one first, and stops as soon as the user acts (`WarmAllPreviews`, `XTPDF_WARM_PREVIEWS=0` disables; ThumbnailCache budget 48 -> 160 MiB, 600 page cap).
- Deep mode, 25 s load wait: jump to END 1397 ms -> 45 ms; zoom-out by 20 notches settle 834 -> 650 ms; peak private memory 696 -> 708 MB. Standard mode: jump to END 44/112 ms, revisit 43 ms. Cost: background CPU for a while after opening a big file (not measured separately).

## What Foxit remembers between sessions (04/10)

- No render cache on disk: %LOCALAPPDATA%\Foxit PhantomPDF holds only crash logs (0.6 MB); %APPDATA%\Foxit Software is WebView/updater data. A re-open being fast is mostly the OS file cache holding the PDF.
- HKCU\Software\Foxit Software\Foxit PhantomPDF 10.0\Preferences\History\LastOpen\N stores per file: FileName, Page, PosX/PosY, Scale, zoomToMode, Mode, panel layout. That is the "remembers the page" behaviour.
- Reader now does the same: `positions.json` (%LOCALAPPDATA%\XTPdfReader) keeps page + zoom mode/zoom per file (300 entries), written 1.2 s after the last change and on close, restored when a file is opened (`ShowFirstPageAsync`). Not restored for files that are already open. Offset inside the page is not stored (page top).

## Thread/CPU profile during a 40-notch zoom roll (04/10, Tests/Profile-GuiThreads.ps1)

Per-thread CPU cycles (QueryThreadCycleTime every 10 ms, passive), same 165 MB file, Ctrl+wheel 40 notches 30 ms apart, 2.5 s window.

| | total cycles | busiest thread's share | intervals with >=2 busy threads |
|---|---|---|---|
| Foxit zoom-in | 1.3 G | 100% (one thread, 26 threads exist) | 0 of 155 |
| Foxit zoom-out | 4.9 G | 99% | 2 of 156 |
| Reader zoom-in | 2.7 G | 29% (spread over ~7 threads) | 22 of 130 |
| Reader zoom-out | 2.1 G | 30% | 22 of 120 |

- Foxit renders on one thread (the main thread: no tile parallelism, no render worker pool). Its smoothness comes from doing less per step, not from more cores.
- Reader spends about twice Foxit's CPU on zoom-in. Switching off the wide region, speculation or preview warming changes it only between 2.7 and 3.5 G, so the extra cost is elsewhere (candidates: bitmap creation/freeze per region, WPF composition and cross-fade frames, GC). Not yet attributed; needs a managed profiler.
- Other Foxit facts from its install/registry: no render cache on disk; HKCU ...\Preferences\Display has bPathSmooth=1, bUseClearType=0 (Reader renders with FPDF_LCD_TEXT); the `Setting` folder only holds PDF-conversion presets.

## Foxit UI-thread stack sampling during zoom (04/10, Tests/Profile-ThreadStacks.ps1 + Ghidra FuncMap/Decomp)

Method: every ~2 ms suspend Foxit's UI thread for a moment (no debugger), read EIP + 4 KB of stack, resume; map EIP and call-preceded return addresses to Ghidra functions (runtime base 0x410000, Ghidra base 0x400000). The binary embeds trace strings with source names, e.g. `CPDF_TVPreview::OnPaint` in `foxitreader\preview.cpp`, so some functions can be named.

- The page view is `CPDF_TVPreview` (OnPaint, ZoomToPreview, GotoPagePreview, CalcPagesHeightAndWidthProc, DrawPagesAnnotAndOthers). `OnPaint` creates a compatible DC + bitmap of the clip box, renders into it, then ONE `BitBlt(SRCCOPY)` to the window: every paint is a complete, atomic frame.
- The page tile render (FUN_01d65d10 -> FUN_01d649a0) allocates a 24 bpp DIB for the rectangle and, in blocking mode, loops `do { Continue() } while (!finished)` on the UI thread. No worker threads (99-100% of CPU in one thread).
- Timeline of the UI thread in a 40-notch, 30 ms-apart Ctrl+wheel roll: back-to-back render blocks of ~85 ms separated by ~10 ms idle gaps (the message loop: input + paint); zoom-in blocks shrink 163 -> 6 ms as the zoom approaches the limit, zoom-out blocks grow 6 -> 174 ms and end with a 588 ms block (final full render). That is about one frame per 95 ms while rolling, i.e. Foxit's cadence is NOT higher than Reader's (Reader: zoom-in ~70 ms, zoom-out ~35 ms per update).
- While a block runs the UI thread is blocked, so queued wheel notches are consumed together afterwards (a natural coalescing); Reader keeps the UI thread free.

## Page 31 zoom roll, Foxit vs Reader frame by frame (04/10)

Capture: Capture-GuiZoomSequence.ps1 (60 fps screen capture of the content area, page 31 reached with 30x PgDn, 40 Ctrl+wheel notches 20 ms apart), analysis: per changed frame the zoom ratio by FFT registration.

- Foxit zoom-in: 21 changed frames over 1.1 s, per-frame ratio 1.09-1.5 (median ~1.25), every frame sharp. Zoom-out: 16 frames over 1.35 s, ratio 0.6-0.9.
- Reader before the change: zoom-in only 5 changed frames in 267 ms with per-frame jumps of x1.5, x1.7 and a large unmeasurable jump (the wheel accelerator I had raised multiplies the zoom by up to 1.08^7 = x1.7 per notch, so about x3-8 per presented frame): the zoom teleports. Zoom-out 14 frames in 534 ms.
- Cause of the different "effect": not image quality but the temporal profile of the zoom (few large jumps vs a continuous glide).
- Fix: the wheel only moves a zoom TARGET (kept within x6 of the shown zoom); the shown zoom glides toward it at most `ZoomRateLimit` = 6 ln/s (XTPDF_ZOOM_RATE, 0 = old behaviour), each step still an exact-size render presented when ready. Single notches (ln 1.08 < 6 x 16 ms) apply immediately, so their 17-30 ms latency is unchanged.
- After: zoom-in 13 frames in 431 ms with ratios 1.07-1.37 (mostly every 16 ms), zoom-out 39 frames in 834 ms with ratios 0.76-0.96. One 116 ms hold remains at the first region-mode (deep zoom) frame, comparable to Foxit's first 163 ms block.

## Zoom out then in: why the picture sharpened late (04/10)

- Scenario script (Capture-GuiZoomSequence.ps1 -Mode script, ZOOM_SCRIPT="I14,w1200,O22,w900,I22,..."), `XTPDF_DEBUG_LOG` events `PRESENT ready/TIMEOUT`.
- Found: after a deep zoom-out, the first zoom-in step at a cold small zoom took 267 ms; the 250 ms present timeout fired and the view committed the logical zoom (x7 ahead) with scaled temporary images that then sharpened - the visible "sharpening" Foxit never shows (Foxit just holds the old picture until the exact frame is ready).
- Also the presented steps could jump x2-x3 (the logical zoom runs ahead while a step renders).
- Fix: present timeout 250 -> 2500 ms (hold the old picture like Foxit; XTPDF_PRESENT_TIMEOUT_MS), and each presented step is at most x1.45 away from the one on screen (`MaxPresentStep`), the intermediate zoom is rendered exactly.
- Result on the 7-roll script: 0 timeouts, 312 presented steps, max step ratio 1.45 (median 1.10), slowest step 154 ms; holds of 80-170 ms remain where a cold deep-zoom region is rendered.

## Same zoom-out/zoom-in script on Foxit, and zoom speed evenness (04/10)

- Foxit, script I14,w1200,O22,w900,I22,w1200,O10,w250,I10,w800,O22,w300,I22 (30 ms notches, page 31): only 70 changed frames in 9.5 s; during every roll the picture changes every 66-134 ms (about 10-12 per second, uniform), with a few holds of 150-230 ms; the long gaps of 0.5-2 s are idle waits. All frames sharp.
- Reader (same script, rate limit 6): 197 changed frames; 16 ms per frame while the zoom is shallow, 80-170 ms holds when a cold deep region is rendered. Zoom velocity (ln/s between presented steps): p10 2.3, median 5.3, p90 9.6 - fast then slow, a 4x spread, which reads as stutter next to Foxit's steady ~2.6.
- Rate limit 3: p10 1.7, median 2.6, p90 4.8; rate 2: 1.3 / 1.8 / 3.2. Default set to 3 (median equal to Foxit's average ~2.6 ln/s, spread 2.8x).

## Corrections and negative results (04/10)

- Foxit reopens at the page it last showed (registry `LastOpen`: page, scale), so my earlier Foxit captures that pressed PgDn x30 right after opening landed on the end of the document (blank/sparse pages) instead of page 31. Capture-GuiZoomSequence.ps1 now sends Fit-width (`-FitKeys`: `^0` Reader, `^2` Foxit) and Home before the PgDn x30. The Foxit frame-cadence numbers (70 changed frames, 66-134 ms) and the UI-thread render-block timeline were taken in that state and are not reliable for page 31; the structural findings (single thread, 24 bpp tile, one BitBlt per paint, source names) are unaffected. Redone with page 31 for the zoom script: Foxit 80 changed frames, Reader 226 (both zoom into white for part of the roll, so counts are only indicative); Foxit holds still 66-135 ms between updates with occasional 300-380 ms holds.
- Tried and rejected (no gain or worse, measured with the zoom-script log, 3-4 runs each): (a) rendering the next two glide steps in the background while a step is in flight - slow steps per run 14/15 without vs 15/5 with, within the run-to-run noise; (b) limiting each presented step by rate x elapsed time to flatten the velocity - produced chains of small steps, each needing a fresh exact render of 60-160 ms (up to 24 slow steps in a run), so the zoom got slower and not smoother.
- Drag-pan test added to Capture-GuiZoomSequence.ps1 (`H<n>` / `V<n>` script steps). The default zoom anchor (window centre) is white on page 31, so a content-rich anchor is still needed before pan comparisons mean anything.
