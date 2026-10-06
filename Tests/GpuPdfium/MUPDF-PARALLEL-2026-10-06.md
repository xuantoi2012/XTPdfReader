# MuPDF parallel viewport experiment

Background-only, page one of the supplied drainage PDF. Production worker,
reader build and local Release configuration were left unchanged.

PyMuPDF does not support multithreaded library use. Benchmark-MuPdfParallel.py
therefore launches 1/2/4 independent persistent Python processes. Coordinator
threads perform pipe I/O only; no MuPDF objects are shared across threads.
Each process builds and retains its own page-one display list, duplicating it.

All render the same centered 1920x1080 viewport at page widths 3000, 6000 and
12000. Horizontal bands have 2px vertical gutters, which are trimmed when
compositing. Five runs per width/count, first excluded; medians of four warm
repeats include worker IPC and Python pixel composition, not WPF presentation.
Full page height is round(width * 842 / 1191). OS caches not flushed, no
controlled machine isolation. User's running reader was not activated/closed.

| Workers | Width 3000 median | Width 6000 median | Worker private commit |
| --- | ---: | ---: | ---: |
| 1 | 371ms | 221ms | 177MiB |
| 2 | 221ms | 137ms | 353MiB |
| 4 | 147ms | 102ms | 705MiB |

At width 12000: 1 worker 109ms, 2 workers 72ms. Raw full results are in ignored
Tests/bin/mupdf-parallel.jsonl. Memory is summed worker private commit after
render, not peak physical RAM or total reader memory.

At width 6000, splitting across two workers changes 54,438/6,220,800 RGB bytes
(0.875%), max channel delta 15. Four workers change 136,020 bytes (2.19%), max
delta 15. Pixel differences were detected despite gutters; do not claim exact
equivalence or assume differences occur only at band seams without a spatial
diff. Parallel mode should not replace the currently successful viewer path
until clipping/AA behavior is understood and visual quality is checked.

## Decision

Parallel raster is feasible and reduces this crop's elapsed work, but simply
duplicating Python workers has a steep memory cost. Keep the working single
worker default. Two workers are the more balanced exploratory setting; four
save another roughly 35ms at width 6000 while adding about 352MiB.

Preferred next architecture experiment is a native MuPDF bridge: interpret once,
retain the display list, clone per-thread contexts with supplied locks, replay
the shared list into separate output devices, and composite atomically. This
avoids creating multiple interpreted page lists, but actual lock contention,
memory use and crop correctness still need measurement. Do not call the same
document/device/context concurrently. It is not available through this Python
bridge and requires a separate C/C++ implementation and licensing review.

Primary references:
https://pymupdf.readthedocs.io/en/latest/recipes-multiprocessing.html
https://mupdf.readthedocs.io/en/1.27.0/reference/c/overview.html
