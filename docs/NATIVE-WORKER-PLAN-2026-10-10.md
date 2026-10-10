# Native MuPDF worker (no Python), plan and log, 2026-10-10

Owner decision (2026-10-10): build the engine as a native worker, step by step. Personal use, not distributed, so the AGPL question stays parked.
Goal: open and read big PDFs on a network drive the way Foxit does (read the blocks that are needed, no local copy of the whole file),
start faster, drop the Python runtime in the end. The rendering engine stays MuPDF (same pictures); this is not a speed-up of the raster itself.

## Current status after continuation (2026-10-10)

Phases 1–4 are implemented locally: incremental metadata, bounded viewing caches,
native OCR, native text/object discovery and removal, and default packaging without
Python. Replacement text/OCR layers remain with the established C# writers.
Builds and test commands are in [the native README](../Native/XtMuPdfWorker/README.md).
The original plan and historical gaps below are retained as a log; current results
and limits are at the end of this file. No commit or release was published.

## Original baseline (the contract to keep)
- `Services/ExperimentalMuPdfViewport.cs` starts `python.exe MuPdfWorker.py` (source: `Tests/GpuPdfium/MuPdfViewportWorker.py`, 413 lines) and talks
  to it over stdin/stdout: one JSON line per request; the reply is one JSON line, and for a render the BGR(A) pixels follow it.
- Requests: render (no `op`): `path, page, fullWidth, fullHeight, displayWidth, annotations, alpha, password, memoryState, nativeListLimit, hidden, rect`;
  `op`: `metadata, words, select, search, searchrange, close, release, memory, stats`.
- Other Python workers: `Services/Ocr/OcrWorker.py` (Tesseract through PyMuPDF), `Services/TextEdit/TextEditWorker.py` (edit text, uses PyMuPDF high level API).
- Behaviours of the Python worker that must be reproduced (they are why the pictures look the way the owner wants):
  minimum line width in pixels (`XTPDF_MIN_LINE_PX`, default 1.2, scaled by fullWidth/displayWidth) and gamma 1.4 on every picture;
  BGR output; display-list cache per (document stamp, page, annotations); raster cache; document cache with limits;
  unembedded TrueType fonts resolved against the Windows font registry; hidden layers mapped through MuPDF's own layer index (probe by bits);
  unsigned signature widgets and sticky-note icons drawn as stamps in a private copy; password; crop of a requested rectangle on a global pixel grid.

## Layout
- Sources: `Native/XtMuPdfWorker/` (`worker.cpp`, `json.h`, `Build.ps1`). Built against the MuPDF SDK shipped inside the PyMuPDF wheel
  (`Tests/bin/mupdf-bench-deps/pymupdf/mupdf-devel`, same as `Tests/MuPdfNativeWorker`), VS 18 C++ tools. Output `Native/XtMuPdfWorker/bin/` (ignored), with `mupdfcpp64.dll`.
- Switch: the app uses the native worker when `xtpdfworker.exe` is next to it (or `XTPDF_NATIVE_WORKER=1`); `XTPDF_NATIVE_WORKER=0` forces Python.
  The Python worker stays as fallback until phase 4.
- Same protocol as above, so the C# side changes only in how the process is started.

## Phase 1: viewing (what network files need)
| Step | Content | Done when |
|---|---|---|
| 1A | Skeleton: request loop, JSON in/out, `metadata`, render of a page and of a crop (BGR, gamma, min line), lazy block stream for network paths (`Tests/MuPdfLazyStream` is the prototype) | A 162 MB file on P: shows page 1 in a few seconds; pictures within a small tolerance of the Python worker on a set of pages |
| 1B | Caches and lifetime: document stamp cache, display lists, rasters, `close/release/memory/stats`, password, `memoryState` limits | A long scroll keeps memory flat; closing a file releases it |
| 1C | Fidelity: hidden layers (layer index probe), annotations flag, notes and unsigned signature widgets as stamps, Windows font substitution | Same pictures as Python on the layer, signature and note test files |
| 1D | Text: `words`, `select`, `search`, `searchrange` (stext, same coordinates and snippets) | Selection, I-beam and search results equal to Python on test pages |
| 1E | C# integration and packaging: start the exe, fallback, copy exe + DLL into the build and the installer, run the bridge tests against it | `--background-regression` and the MuPDF bridge checks pass with the native worker; file on P: opens without the local copy (`RemoteFileStage` bypassed for native) |

## Phase 2: OCR
Port `OcrWorker.py` (tiles, `get_textpage_ocr` equivalent through the C API: stext OCR device with tessdata `vie`). Done when OCR results match on the test scans.

## Phase 3: text editing
Port `TextEditWorker.py` (find text run, remove glyphs, write new text with the right font). The biggest piece; only after 1 and 2 are in daily use.

## Phase 4: remove Python
Package without `MuPdfRuntime` (about 61 MB), remove the `.py` files, drop `RemoteFileStage` when the native path is the only one.

## Risks and rules
- A native crash must not take the app down: the worker stays a separate process (the Python worker's isolation is a feature we keep).
- Errors inside MuPDF are caught with `fz_try/fz_catch` in the worker and sent as `{"error": "..."}`; never unwind across the process boundary.
- Each step is compared against the Python worker on real files (the drawing sets, the signature and layer test files) before the next one starts.
- Licence: MuPDF and iText are AGPL; personal use only for now. Revisit before anyone else receives a build.

## Log
- 2026-10-10: plan written. Prototype `Tests/MuPdfLazyStream` measured on the 162 MB file on P: (256 KB blocks): open 0.04 s, page 1 bounds 1.8 s, page 1 rendered 0.2 s, all 277 sizes 11.8 s in the background.
- 2026-10-10, step 1A done (`Native/XtMuPdfWorker`: `worker.cpp`, `json.h`, `Build.ps1`, `compare.py`; not wired into the app yet).
  - Implemented: request loop and JSON, `metadata`, render of a page or a crop (BGR/BGRA, min line width, gamma, global pixel grid), document and display-list caches with limits,
    `close/release`, password, lazy 256 KB block reading for network paths (LRU, 256 MB budget; env `XTPDF_BLOCK_KB`, `XTPDF_BLOCK_BUDGET_MB`). `words/select/search/searchrange` answer an error for now.
  - `compare.py <file>` runs the same requests through the Python worker and the native one. Local copy of the 277-page file: page sizes equal (0.000 pt); full-page and crop renders byte-identical
    on pages 1 and 139; page 277 differs by 0.6-1.4 % of bytes (expected: Windows font substitution is step 1C).
  - On P: (162 MB file): `metadata` of all 277 pages 31.1 s (Python) vs 12.5 s (native); first request = render of page 1 on the 173 MB file 5.9 s (Python) vs 2.9 s (native). Page renders afterwards 0.01-0.09 s.
  - Learned: `fz_run_display_list` takes the scissor in list (page) space, not device space; `fz_identity`/`fz_infinite_rect` are not exported by the DLL (build the values locally).
  - Next (1B): `memory/stats` with real numbers, raster cache, memoryState limits; then 1C (layers, stamps, fonts). For 1E: make `metadata` incremental (count + the sizes of a range first) so the page can show before all sizes are read.
- 2026-10-10, steps 1B-1E done in one pass (all verified against the Python worker with `Native/XtMuPdfWorker/compare.py`):
  - 1B: `memory`, `stats`, `close/release`, per-request limits from `memoryState`/`nativeListLimit`, document and display-list LRU. Raster cache not ported (the app caches pictures itself; the Python cache was for repeated identical requests).
  - 1C (`fidelity.h`): hidden layers by the probe of MuPDF's layer index, Windows font substitution for unembedded TrueType, unsigned signature widgets and sticky notes as stamps. Page 277 of the 277-page file, which differed by 1.4 % before, is now byte-identical. Layer test: results identical in both workers (hiding half of 740 OCGs changed nothing on the three sampled pages in either worker, so the effect itself is not demonstrated by that file; a file where layers change the picture should be added).
  - 1D: `words`, `select`, `search`, `searchrange`: same counts, same texts, box difference at most 0.0009 of the page width, on the 277-page file and on a 4-page file rotated 0/90/180/270 (all identical).
  - 1E: `ExperimentalMuPdfViewport.StartWorker` starts `xtpdfworker.exe` when it sits beside the app with `mupdfcpp64.dll` (`XTPDF_NATIVE_WORKER=0` forces Python); `RemoteFileStage` is bypassed then; `metadata` may take 240 s instead of 30 s.
    csproj copies both files when `Native/XtMuPdfWorker/bin/xtpdfworker.exe` exists (build it with `Native/XtMuPdfWorker/Build.ps1` before the release build).
    Through the app's own bridge on the 173 MB file on P:: no copy, 332 sizes in 8.5 s. `--mupdf-migration-check` 91 checks PASS, `--mupdf-viewport-check` 27 PASS with the native worker.
  - Not done: incremental `metadata` (the page waits for all sizes: 8-12 s on the big files on P:), raster cache, an A/B check on a file whose layers change the picture, OCR and text edit (phases 2 and 3), removal of Python (phase 4). `--background-regression` could not run here (no pdfium.dll next to the test exe): not caused by this work.

## Continuation: bounded raster cache and transparency regression (2026-10-10)

- Pulled the current branch to `d723445`, then continued the viewing phase.
- Added a worker-local raster LRU, default **128 MiB per worker**, configurable with
  `XTPDF_MUPDF_RASTER_MB` (0–512). `XTPDF_MUPDF_NO_RASTER_CACHE=1` disables it,
  matching the Python worker's diagnostic switch. Oversized pictures are returned without retention.
- Cache keys include the authenticated document stamp (path/time/size/password/layers), page,
  annotations, alpha, full dimensions, crop and full-precision display width. Document eviction,
  replacement, close and release remove associated rasters. Memory states 1 and 2 disable retention
  and clear rasters; an explicit `memory` request clears them even for protected visible pages.
- `stats` now reports actual `rasterBytes` and cumulative `rasterHits`/`rasterMisses`.
  `storeBytes` remains the existing placeholder; this change does not measure MuPDF's internal store.
- Fixed transparent renders: `fz_clear_pixmap_with_value(..., 0)` made the background opaque black.
  Alpha output now uses `fz_clear_pixmap`, matching the Python worker byte for byte on the fixture.
- Added `Native/XtMuPdfWorker/test_cache.py`. It generates a temporary PDF with a visibly changing
  OCG and sticky notes, compares seven render variants against Python, and checks repeated hits,
  1 MiB LRU eviction, close/release, memory pressure, disabled caching and oversized bypass.
  The variants cover page, annotations, alpha, display width, crop/dimensions and hidden layers.

### Validation

```powershell
& Native/XtMuPdfWorker/Build.ps1
python Native/XtMuPdfWorker/test_cache.py
dotnet build Tests/PerformanceTests.csproj -c Release -o Tests/bin/NativeCacheRegression
$env:XTPDF_NATIVE_WORKER = '1'
dotnet Tests/bin/NativeCacheRegression/XTPdfMergeApp.PerformanceTests.dll --mupdf-migration-check
# Pass the real drainage drawing path from Tests/GpuPdfium/Start-MuPdfViewportTrial.ps1:
dotnet Tests/bin/NativeCacheRegression/XTPdfMergeApp.PerformanceTests.dll --mupdf-viewport-check <drainage.pdf>
```

Native build and Python cache regressions PASS. Release build: 0 errors, 25 warnings in unchanged C#.
Migration: **91 checks PASS**. Viewport: **27 checks PASS**, with the existing drainage PDF on T:,
read only (cancellation, protocol recovery and restart included). The viewport check needs a drawing
with ink in its fixed central crop; the mostly blank migration LRU fixture is unsuitable.
No full background regression or long-scroll soak was run in this continuation.

### Next

Incremental metadata and C# layout integration remain the next opening-latency task: show the first
page while remaining sizes load. Then validate long scrolling and aggregate memory across workers;
the raster budget above is per process, in addition to the app bitmap cache. OCR, native text editing
and eventual Python removal remain phases 2–4. The visible-layer A/B gap is now covered by the
generated fixture; broader real-file fidelity comparisons remain useful.

## Continuation: complete native jobs and Python-free packaging (2026-10-10)

### Implementation

- `metadata` accepts a zero-based `{first,count}` range. C# reads the selected
  page size first, then loads remaining sizes in batches of 16 so rendering can
  use the command lane between batches. Aspect lookups request one page.
- Network block cache defaults to 64 MiB, clamps settings, and tracks its bytes.
  Pressure state 1 trims it to at most 4 MiB; state 2 retains only a pinned stream
  block. Never evict that block while MuPDF holds stream pointers to it.
- Raster keys on both sides include the display-width hint, which affects minimum
  line width. The previous cache could reuse pixels rendered with another hint.
- `--ocr` implements full-page/tiled OCR and region OCR using MuPDF's native
  Tesseract PDFOCR writer followed by native text extraction. Preserves tile
  overlap ownership, normalized coordinates, page rotation, skip-text behavior,
  per-page errors and progress. C# checks completion/exit status, supports password
  jobs and deletes temporary job files even if process startup fails.
- `--textedit` implements all existing modes: runs, area, pick, pickArea,
  areaObjects, apply and delete. Glyph removal preserves images/vector graphics;
  drawn-object removal preserves text and redraws contained surviving paths.
  Saves remain incremental on copies created by the app's pending-edit flow.
- Real CAD comparisons caught three fidelity details now covered: whitespace span
  merging, repaired glyph bounds for fonts with short ascender/descender metrics,
  and path classification/bounds for implicit closures and unused moveto commands.
  Object discovery includes annotation appearances, matching the Python baseline.
- Default `NativeOnly=true` builds auto-build/copy the worker and omit all three
  Python scripts and MuPdfRuntime. Release packaging defaults to native; Python
  fallback remains opt-in with `-WithPython` / `-p:NativeOnly=false`.
  SDK and DLL remain paired; Build.ps1 discovers installed C++ tools with vswhere.
- UI tests now use an isolated print inbox and derive a persisted ribbon choice
  from the actual split button instead of assuming the user's saved choice.

### Results

All checks below passed on Windows x64 with the PyMuPDF 1.28.2 SDK/DLL:

| Check | Result |
|---|---|
| Native C++ and Release C# builds | No errors; existing C# warnings remain |
| Python-free installation + incremental metadata | 56 checks |
| Final native installation/metadata/OCR/text/object selection and save suite | 153 checks |
| Full UI smoke | 681 checks; final glyph/path refinements subsequently covered by targeted suite + A/B |
| Reader memory regression | 71 checks |
| Migration / real drainage viewport on T: | 91 / 27 checks |
| Native/Python viewing on 277-page, 170,261,791-byte PDF on P: | All 277 sizes equal; sampled full/crop images byte-identical on pages 1, 139, 277; selection/search texts equal |
| Native/Python real text/object extraction on P: | First/middle/last page text styles/geometry and sampled object regions match; source size/time unchanged |
| Generated rotated text/object saves | Same saved text, image/path counts and pixels as Python, including repeated image uses and annotations |
| Native/Python OCR | Four scenarios, identical words and normalized boxes; includes tiles, rotations, regions and invalid-page recovery |
| Raster cache fidelity/lifetime | Pixel parity, visible OCG changes, alpha, annotations, hint/crop keys, LRU, close/release and pressure all pass |
| Repeated-render soak | 4 × 64 pages, private memory 42.7, 42.7, 42.9, 42.9 MiB; bounded list/raster/block caches and revision invalidation pass |
| Clean native Reader publish | Approximately 40.3 MiB; exe + MuPDF DLL + Vietnamese tessdata present; no Python exe/scripts/runtime |

Real viewing A/B timings were measured with files already warm in OS/network caches;
they are fidelity checks, not evidence of cold-open speed. The 43 MiB memory result
is for the generated soak fixture, not every real drawing (the real viewport worker
used about 160 MiB). The native-only Reader publish requires .NET 10 Desktop Runtime.

### Remaining validation limits

The legacy full `--background-regression` still depends on PDFium and was not run
for this native-only build. No multi-hour real-file/multi-worker soak, installer
installation or release upload was performed. `storeBytes` is still a placeholder
rather than a measurement of MuPDF's internal store. The read-only real-file A/B
checks sample text/objects/renders; they do not establish fidelity for every PDF.

## Further performance work: shared pixels and native abort (2026-10-10)

The owner authorized further native/app optimization and no longer requires a
Foxit comparison. See [performance measurements](NATIVE-PERFORMANCE-2026-10-10.md)
for reproducible before/after benchmarks and current targets.

- Windows shared mappings replace binary pixel transfer and the intermediate
  managed array on the app's native path. Binary fallback remains supported.
- Request-scoped cancellation events interrupt MuPDF page/list/raster work while
  preserving the process and its complete display lists. Partial output is discarded.
- Render reservations reflect fewer pixel planes, allowing the two existing visible
  lanes to run concurrently within the memory budget. Mapping capacity is explicitly
  included in adaptive RAM accounting and released/shrunk when appropriate.
- Measured on the real CAD file: two adjacent 4608 px pages **212 → 144 ms**;
  cancelled heavy frame **226 → 61 ms** (cancel requested at 35 ms);
  managed allocation for an 8192 px frame **142 MB → approximately 14 KB**.
  Allocation reduction is not the same as total RAM savings.
- Validation: 98 native integration/metadata checks, 71 reader-memory checks and
  full UI smoke **681 checks PASS**. Native cancellation fixture: 38 cancelled
  frames, unchanged pixels after recovery and stable handle count (64 → 64).
  Cache/alpha/layer/fallback and real render comparisons pass; no multi-hour soak.
- Publish the Reader plus XT Capture to `Tests/bin/NativeOptimizedPublish` for the
  owner's next trial, leaving already-running earlier trial copies alone.
