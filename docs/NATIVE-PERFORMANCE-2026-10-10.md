# Native reader performance: measurements and next targets

The intended workload is the owner's large CAD drawing PDFs, including network
drives. The owner now wants internal before/after optimization and will evaluate
the resulting application directly; a Foxit benchmark is no longer required.
The app and its native worker are under our control; the underlying MuPDF raster
library is still the 1.28.2 SDK DLL, not a modified MuPDF source build.

## Implemented: draw into the outgoing pixel buffer

Previously every uncached frame allocated an output buffer and a MuPDF pixmap,
cleared both, rendered into the pixmap, and copied the entire raster to the output.
When the transformed crop exactly matches the requested integer bounds, MuPDF now
borrows the output buffer and renders into it directly. The pixmap is dropped
before the buffer is moved into the cache or sent over the pipe.

Fractional bounds retain the existing overlap-copy path so dimensions, clipping
and padding stay identical. Gamma, alpha, minimum line width and annotation
rendering are unchanged. This removes one full image allocation and copy on the
direct path; it does not remove pipe transfer or WPF's own image copy.

Measured on page 277 of the real 277-page network PDF, with file/display lists
warm, raster caches disabled, two discarded warmups and 12 measured samples per
case. Times include rendering and reading the complete pixel reply in the Python
benchmark client; **they exclude WPF presentation and UI frame timing**.

| Case | Previous p50 | Direct-buffer p50 | Reduction |
|---|---:|---:|---:|
| Full page, 1200 px | 45.24 ms | 44.15 ms | 2.4% |
| Full page, 4608 px | 152.25 ms | 141.22 ms | 7.2% |
| Full page, 8192 px | 310.21 ms | 272.88 ms | 12.0% |
| 1201 × 801 crop | 13.57 ms | 12.84 ms | 5.4% |
| Same crop with alpha | 14.37 ms | 13.52 ms | 5.9% |

Reverse-order repeat also favored the direct path: 4608 px approximately
152.19 → 141.08 ms, 8192 px approximately 342.33 → 272.24 ms. Timing variability
is visible; this is evidence for the tested workload, not a universal speedup.
All five cases have identical SHA-256 pixel hashes between binaries and repeats.
The 4608/8192 frames contain 45,052,416/142,368,768 bytes, respectively; the avoided
extra image buffers are approximately 43/136 MiB (allocation sizes, not measured
whole-process peak savings).

## Measurement support

`Native/XtMuPdfWorker/bench_render.py` compares two binaries and writes JSON with
p50/p95 wall and raster timings, sample counts and pixel hashes. It verifies the
source PDF's size/time remain unchanged. Preserve the old worker before building
the candidate; both binaries need the matching DLL beside them:

```powershell
python Native/XtMuPdfWorker/bench_render.py <drawing.pdf> --page 277 --baseline <old-worker.exe> --candidate Native/XtMuPdfWorker/bin/xtpdfworker.exe --output Tests/bin/render-bench.json
```

The app's existing diagnostics now record native scheduling wait, worker preparation,
complete worker/pixel round trip and WPF image creation. Cache hits bypass these
render/copy counters. This makes it possible to distinguish raster cost from
queueing, transport and presentation before changing the next stage.

Validation: cache/pixel parity PASS; real-file full/crop/layer parity PASS;
256-render lifetime regression PASS (private memory 42.5 MiB after each sweep).
The bridge timing regression, real drainage viewport and reader memory checks
are also run against the new build.

## Implemented next: shared transport, native cancellation and concurrency

Native viewing now draws into a bounded Windows page-file mapping and returns its
name in the JSON header. WPF copies directly from a read-only mapping view while
holding the worker gate. There is no stdout pixel payload or managed intermediate
pixel array. Binary transport is the fallback. The final WPF image owns its pixels,
so resizing/reusing/closing the mapping does not change displayed images.

Mappings are limited to 256 MiB per worker, reused for compatible sizes and shrunk
when more than four times the next output size (with a 16 MiB reuse floor). Memory,
close, release and cancelled renders free the mapping. The adaptive memory policy
adds explicitly reported mapping capacity to worker private bytes, because shareable
allocations cannot be assumed to appear in private-process counters.

Active render reservations now estimate two CPU pixel planes for shared transport,
three for native binary transport and four for legacy rendering. This lets the two
existing foreground lanes run simultaneously when the reduced memory footprint fits
the configured budget. No extra rendering processes were added.

The app also creates a unique cancellation event for each native render. A Windows
wait-pool callback sets MuPDF's abort cookie during page interpretation/display-list
construction and rasterization. No polling thread or process restart is needed.
Partial lists and pixels are discarded. The app drains the error response before
releasing the worker gate. Cancellation remains cooperative: a synchronous file
read or one long MuPDF operation can delay reaching an abort check.

Measured through the actual C# → native → WPF path on the same warm CAD PDF:

| Check | Binary pipe | Shared/native path |
|---|---:|---:|
| 4608 px frame p50, 12 samples | 146.02 ms | 144.08 ms |
| 8192 px frame p50, 12 samples | 286.22 ms | 279.04 ms |
| .NET allocation per 4608 px frame | 45,076,479 bytes | 16,197 bytes |
| .NET allocation per 8192 px frame | 142,390,260 bytes | 14,267 bytes |
| Two adjacent 4608 px pages, p50, 8 batches, MemorySaving profile | 212.07 ms | 143.60 ms |
| Heavy render cancelled after 35 ms, p50, 8 samples | 225.71 ms without native abort | 61.24 ms with native abort |

The pair throughput improved about 32% on this workload. Single-frame latency gains
are modest; the main transport benefit is avoiding large managed allocations.
The 8192 px p95 varied from 291.21 to 295.24 ms, so this measurement does not establish
an improvement in every latency percentile. Allocation counts exclude deliberate
pixel-hash extraction, but include allocations performed during the render calls.
These are allocation rates, not claims about total application RAM savings.

Commands (run benchmarks alone, without another CPU-heavy test):

```powershell
dotnet Tests/bin/NativeTransportRegression/XTPdfMergeApp.PerformanceTests.dll --native-transport-bench <drawing.pdf> 277
dotnet Tests/bin/NativeTransportRegression/XTPdfMergeApp.PerformanceTests.dll --native-cancel-bench <drawing.pdf> 277
dotnet Tests/bin/NativeTransportRegression/XTPdfMergeApp.PerformanceTests.dll --test TestNativeSharedTransportAsync,TestNativeRenderTimingsAsync,TestNativeOnlyInstallation,TestNativeMetadataAsync
python Native/XtMuPdfWorker/test_cancel.py
```

Validation so far: 98 native integration/timing/metadata checks and 71 reader-memory
checks PASS. Shared/pipe pixels match, including alpha and fractional crops; earlier
WPF images survive mapping reuse/resizing; 16 concurrent requests preserve ordering
and ownership; close and restart clean up mapping accounting. The native cancellation
fixture passes 38 aborts with no partial cached lists/frames, same-worker recovery,
and stable process handle counts (64 → 64). Cache parity/fallback, 256-render lifetime
and real-file full/crop/layer A/B checks pass. Full UI smoke: **681 checks PASS**
on the new transport/cancellation build. No multi-hour user-navigation soak was run.

## Priorities for further iterations

1. Keep repeatable app baselines on the same files, viewport, DPI,
   zoom and cache state. Measure cold/warm first meaningful content, time to a
   sharp visible page, input-to-frame latency, p95/p99 frame intervals and memory.
2. Profile progressive presentation and queue cancellation: keep existing content
   responsive, show useful previews promptly, prioritize visible crops, and avoid
   spending CPU on obsolete zoom/scroll positions. A 60 Hz UI frame budget is
   16.7 ms; full-page rasterization does not need to complete inside that budget.
3. Profile remaining WPF composition/upload and cache behavior. Shared transport
   is now implemented; it still requires WPF's final image copy and GPU upload.
4. Tune tile size, page affinity and directional prefetch using measured navigation
   traces. More worker processes can increase duplicate parsing and memory; do not
   add workers without evidence of a scheduling bottleneck.
5. Consider a custom MuPDF build or a different presentation backend only after
   identifying costs the worker/app pipeline cannot remove. GPU composition alone
   does not demonstrate faster CAD PDF rasterization.

Every iteration should preserve image fidelity and compare before/after latency
distributions. Changes are accepted on measured benefit and the owner's experience
with actual documents, rather than a claim about all PDF workloads.

## Source-built MuPDF 1.28.2 with banded multi-threaded rendering (2026-10-10)

The worker can now be linked statically against a MuPDF 1.28.2 source tree carrying a small patch
(`Native/XtMuPdfWorker/mupdf-xt/`, build with `Build-Static.ps1`). Why this and not a GPU: the cost of
a dense CAD page is vector rasterisation on the CPU; the GPU compositor experiment
(`GPU-COMPOSITOR-EXPERIMENT-2026-10-10.md`) showed no benefit, and parallelising the CPU rasteriser was
the first step that moved the numbers.

### What was found

* One MuPDF page render is single-threaded; the machine has 28 logical cores.
* Splitting a render into bands changed pixels (0.3 % of bytes, up to 119 levels) because MuPDF
  re-creates edges at the clip lines (`clip_lerp_y`), clips dashes to the scissor with float phase,
  builds shading meshes from the clip, steps shading triangles and the affine texture walk from the first
  clipped row, and decodes images for the clipped area. Each of those was made to follow the scissor of
  the whole-area render instead ("virtual scissor"), so a band draws exactly its part of the full picture.
* Eight threads then stalled on `FZ_LOCK_ALLOC`, which MuPDF takes for every reference count change
  (millions per page): replaced by atomics; plus the per-object ICC link lookup and path keep.

### Measured (real 93 MB drainage drawing on T:, 28-core machine, 8 render threads, warm)

All cases below produced **identical pixels** to the worker linked against PyMuPDF's `mupdfcpp64.dll`.

| Page | Request | DLL worker | Static, banded |
|---|---|---:|---:|
| 3 | full 4608 px | 578 ms | 127 ms |
| 3 | full 8192 px | 876 ms | 261 ms |
| 1 (densest) | full 4608 px | 2197 ms | 590 ms |
| 1 | full 8192 px | 2615 ms | 649 ms |
| 12 | full 4608 px | 271 ms | 60 ms |
| 12 | full 8192 px | 478 ms | 91 ms |
| 3 / 1 / 12 | viewport crop 1201 x 801 | 5.5 / 21 / 33 ms | 5.6 / 21 / 29 ms (single thread, unchanged) |

`full 1200 px` renders of ~1 Mpx stay single-threaded (about 5-15 % faster from the build flags alone).

Through the app (`--native-transport-bench`, page 3, shared transport): 4608 px frame 550 -> 181 ms,
8192 px 846 -> 307 ms, two adjacent 4608 px pages 578 -> 233 ms (`XTPDF_RENDER_THREADS=1` gives the old
numbers with the same exe). `--mupdf-migration-check` 91 PASS, `--mupdf-viewport-check` 27 PASS.
Worker suites `test_cache`, `test_cancel`, `test_edit`, `test_ocr` PASS.

### Limits

* Every band scans the whole display list; below `XTPDF_BAND_MIN_PIXELS` (2 Mpx) one thread is faster.
* A profile guided build was 5-15 % faster still but corrupted the heap when a render was cancelled
  (setjmp/longjmp); it is not used.
* No multi-hour soak, no UI smoke run with this worker yet, and `Build-Release.ps1` was only edited,
  not run.
* The patch is tied to MuPDF 1.28.2.

### Time to sharp pixels in the Reader (`--zoom-sharp-bench`, 2026-10-10)

`Tests/ZoomSharpBench.cs` drives a real `ReaderWindow` (zoom steps up/down, then pans) and measures the time
until `ContinuousPdfView.SharpAtCurrentZoom` is true. Most steps are already sharp at once (page image and
regions with headroom cover them); the cost is in steps that cross to a new resolution on a dense page.
Heaviest page, zoom x1.5 to 2.18: 532 ms with one render thread, ~185 ms with eight; later steps 100-130 ms.
Walking the display list with borrowed references (`list-device.c`) made a crop on a dense page 20 -> 12 ms
and a 2400x1600 crop 71 -> 53 ms. What remains is the render of the new region itself; presenting it in
centre-first tiles (as the PDFium path already does) is the next step and is a UI change.

### Pages that arrive by scrolling (2026-10-10)

`--zoom-sharp-bench` with `XTPDF_BENCH_MODE=scroll` (optional `XTPDF_BENCH_GAP`, `XTPDF_BENCH_JUMP`,
`XTPDF_BENCH_ZOOM`, `XTPDF_BENCH_PAGE`) jumps to the following pages and times the first sharp frame.
Two causes were found for a new page costing 300-400 ms: the Reader always asked for the reusable 4608 px
image (3-4x the pixels the screen shows), and the display list of a dense page takes 100-470 ms to build
in a single thread. Now `RenderFullPageAsync` renders at the shown size first (visible and prefetch
requests; `XTPDF_SMALL_FIRST=0` switches back) and, for visible pages, prepares the 4608 px image in the
background once the view has settled. Cold first page 331 -> 250 ms; when paging every 250-400 ms the view
is caught up after about two pages instead of three or four. The display list build itself is unchanged
and is now the floor for a page that has not been seen.

### More background lanes and deeper prefetch (2026-10-10)

Preparing a page is single-threaded parsing, so the gap when paging fast was the number of lanes that can
parse at once, not the rasteriser. Background lanes are now `min(4, cores / 6)` (at least 2;
`XTPDF_BACKGROUND_LANES` 1-6) and `PrefetchPageCount` is 8 (profile caps: Balance 4, Maximum 8, MemorySaving 1).
Prefetched pages are now rendered at the shown size, so each costs a few MB instead of ~45 MB.
Page-jump bench (`XTPDF_BENCH_MODE=scroll`, zoom 1.0, same drawing):

| Page every | 2 lanes (median / p90) | 4 lanes (median / p90) |
|---|---|---|
| 100 ms | 141 / 173 ms | 31 / 32 ms |
| 150 ms | 108 / 157 ms | 31 / 110 ms |
| 250 ms | 32 / 141 ms | 31 / 32 ms |

Only the first, cold page (~270 ms) and an occasional page remain slow. Cost: up to four more worker
processes (about 170 MB each while busy); idle background workers are retired by the adaptive memory
controller as before, and under memory pressure the lane count drops to 1 and then 0.

### What a cold page costs (2026-10-10)

Building the display list of a page that has not been seen is content-stream interpretation, not loading
or recording: page 1 (dense) 493 ms to a list, 413 ms of that with a device that discards everything; page 3
95 ms. A 1 ms RIP sampler put heap allocation at ~17 % and the lexer/inflate at most of the rest. mimalloc as
MuPDF's allocator made list builds 11-13 % faster (page 1 469 -> 407 ms, page 12 46 -> 36 ms) and the worker
uses ~15 % more memory (169 -> 196 MiB private). Parsing itself is single-threaded per document; the lanes
(several worker processes) are what parallelise it.

### First render of a page used one thread (2026-10-10)

Splitting a cold request into worker time showed the render of a never-seen page at 1500 px taking 141-156 ms
while the same page at 3000 px with the list cached took 52 ms: the page fell under the "cost not known
yet, area below 2 Mpx" rule and ran on one thread. The time the page's own display list took to build is
known just before the render and tracks its raster cost (about 1.5 ms per megapixel for each ms of build);
whole-page requests now use it as the first estimate. Cold page 20 at 1500 px: 194 -> 85 ms
(list 52 + render 31); first page of the densest sheet at 1000 px: 937 -> ~620 ms (list 433 of that).
What is left of a cold page is the single-threaded content-stream interpretation.
