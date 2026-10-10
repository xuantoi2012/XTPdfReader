# MuPDF 1.28.2 with the XT patch

`mupdf-1.28.2-xt.patch` is a patch against the stock **MuPDF 1.28.2** source tree
(`mupdf-1.28.2-source.tar.gz` from https://mupdf.com/downloads/archive/). It is built by
`../Build-Static.ps1` into `xtpdfworker.exe` with MuPDF linked in statically (no `mupdfcpp64.dll`,
no PyMuPDF at run time).

**The rule: every picture is byte for byte what the unpatched engine produces.** The patch changes how
the work is split and synchronised, never what is drawn. MuPDF is AGPL-3.0 and this patch is a derivative work, so it is AGPL-3.0 too. Renaming the executable
(`xtpdfworker.exe`) or forking the tree does not change that, and the copyright and licence notices in
the MuPDF files must stay. Personal use that is not distributed is fine (see
`docs/NATIVE-WORKER-PLAN-2026-10-10.md`). Before any build goes to someone else, either buy a commercial
licence from Artifex, publish the corresponding source of the combined work under AGPL, or replace the
engine.

## What it changes

| Area | Files | Purpose |
|---|---|---|
| Banded rendering ("virtual scissor") | `draw-device.c`, `draw-edge.c`, `draw-mesh.c`, `draw-affine.c`, `draw-imp.h`, `shade.h` | A device can draw one horizontal band of a larger canvas and still make every geometric decision exactly as a render of the whole canvas would (edge clipping, dash culling, tile ranges, group / mask extents, shading mesh, rotated-image texture walk, image decode area). `fz_draw_set_virtual_scissor(ctx, dev, whole)`. |
| Lock-free reference counts | `context.h`, `store.c`, `path.c` | `fz_keep_imp*` / `fz_drop_imp*` and the store's own counters use compare-and-swap instead of `FZ_LOCK_ALLOC`. Without this eight threads spend their time on one mutex. |
| No allocator lock | `memory.c` | `fz_set_alloc_lockless(1)` skips `FZ_LOCK_ALLOC` around malloc/free (the CRT heap is thread safe); scavenging still takes the lock. |
| Borrowed references while walking the list | `list-device.c` | `fz_run_display_list` kept and dropped the colorspace, stroke and path of every node (atomic operations) although the list owns them; skipping a node is now about half as expensive, which matters because every band and every crop walks the whole list. |
| Per-thread ICC link cache | `colorspace.c` | `resolve_color` asked the store for an ICC link for every painted object. |
| Store fast paths | `store.c` | `fz_drop_storable` / `fz_drop_key_storable` decrement without the global lock while nothing can be scavenged. |
| `exact_y` edge clipping | `draw-edge.c` | `fz_set_gel_exact_y(1)`: edges are not re-created at y clip lines. **Off by default** and not used by the worker (it changes pixels of nested clips by a few levels); kept as an experiment. |

All new behaviour is off unless the worker asks for it, and the non-banded path is the original code.

## How the worker uses it (`worker.cpp`, `XT_PATCHED_MUPDF`)

Renders written straight into the output buffer (`direct`) are banded when they are expensive: after the
first render of a page its cost per megapixel is known (one value for whole pages, one per cell of an 8 x 8
grid for tiles) and bands are used from `XTPDF_BAND_MIN_MS` (default 25 ms of estimated single-thread
work); before that, areas of at least `XTPDF_BAND_MIN_PIXELS` (default 2 000 000 px) are banded. The
estimate is only a speed heuristic and never changes pixels. Large renders are split into horizontal bands drawn by `XTPDF_RENDER_THREADS` threads (default
min(8, cores)). Each thread owns a cloned context and its own cookie; cancellation is checked between
bands. The display list of each band is culled with a margin of `8 + 12 * minLineWidthPx` pixels
(`XTPDF_BAND_MARGIN_PX` overrides) so hairlines whose recorded bounds miss the band are still drawn.
Smaller requests (viewport tiles) stay single-threaded: every band scans the whole list, so banding a
small area costs more than it saves.

## Checks that must pass after any change

* `Native/XtMuPdfWorker/bench_render.py <real.pdf> --page N --baseline <worker with mupdfcpp64.dll> --candidate bin/static/xtpdfworker.exe`
  must print `identical pixels` for full pages (1200 / 4608 / 8192 px), a crop and an alpha crop,
  on several pages and with `XTPDF_RENDER_THREADS` = 1, 2, 4, 8, 16.
* `test_cache.py`, `test_cancel.py`, `test_edit.py`, `test_ocr.py` (they compare against the Python worker).
* A drawing with transparency groups, soft masks, shadings, tiling patterns, clipped text, dashes, rotated
  images and blend modes. `mkfeat.py` (writes such a PDF) caught four real differences during development
  (soft-mask shading mesh, shading triangle stepping, rotated-image texture start, image decode area);
  a CAD sheet alone did not.

## Third-party

`Build-Static.ps1` also links **mimalloc** (MIT licence, Microsoft; https://github.com/microsoft/mimalloc,
tested with v2.1.7, expected at `..\..\mimalloc-src\mimalloc-2.1.7` or `XTPDF_MIMALLOC_SRC`) as MuPDF's
allocator. It builds display lists about 12 % faster than the CRT heap (page 1: 469 -> 407 ms) and removes
allocator contention between render threads; the worker uses about 15 % more private memory. Without the
source the worker is built with the CRT heap.

## Known limits

* **No PGO.** A profile guided build was 5-15 % faster but corrupted the heap when a render was cancelled
  (also single-threaded). MuPDF unwinds with setjmp/longjmp, which PGO optimises unsafely. Do not enable it.

* Every band scans the whole display list. A very dense page at a small size gains little; the worker
  therefore keeps the single-thread path below the pixel threshold.
* Objects that span many bands (long hairlines) are processed once per band they touch.
* The patch targets exactly 1.28.2. Moving to another MuPDF version means re-applying and re-testing it.
* Thread safety was checked on drawings and the feature file; there has been no multi-hour soak.
