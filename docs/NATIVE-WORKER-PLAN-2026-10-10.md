# Native MuPDF worker (no Python), plan and log, 2026-10-10

Owner decision (2026-10-10): build the engine as a native worker, step by step. Personal use, not distributed, so the AGPL question stays parked.
Goal: open and read big PDFs on a network drive the way Foxit does (read the blocks that are needed, no local copy of the whole file),
start faster, drop the Python runtime in the end. The rendering engine stays MuPDF (same pictures); this is not a speed-up of the raster itself.

## What exists today (the contract to keep)
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
- Switch: the app uses the native worker when `xtmupdfworker.exe` is next to it (or `XTPDF_NATIVE_WORKER=1`); `XTPDF_NATIVE_WORKER=0` forces Python.
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
  - 1E: `ExperimentalMuPdfViewport.StartWorker` starts `xtmupdfworker.exe` when it sits beside the app with `mupdfcpp64.dll` (`XTPDF_NATIVE_WORKER=0` forces Python); `RemoteFileStage` is bypassed then; `metadata` may take 240 s instead of 30 s.
    csproj copies both files when `Native/XtMuPdfWorker/bin/xtmupdfworker.exe` exists (build it with `Native/XtMuPdfWorker/Build.ps1` before the release build).
    Through the app's own bridge on the 173 MB file on P:: no copy, 332 sizes in 8.5 s. `--mupdf-migration-check` 91 checks PASS, `--mupdf-viewport-check` 27 PASS with the native worker.
  - Not done: incremental `metadata` (the page waits for all sizes: 8-12 s on the big files on P:), raster cache, an A/B check on a file whose layers change the picture, OCR and text edit (phases 2 and 3), removal of Python (phase 4). `--background-regression` could not run here (no pdfium.dll next to the test exe): not caused by this work.
