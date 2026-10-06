# Viewport worker experiment - 2026-10-06

## Result and default

The reader now uses one native viewport crop by default rather than splitting
it into 640-pixel tiles that are processed sequentially and only displayed
after composition. Since tile publication was already disabled, the old
split performed extra native traversals without an earlier visible update.
Cached images remain visible during zoom and completed crops replace them
without fading. Existing pan-overlap reuse is retained.

`XTPDF_VIEWPORT_WORKERS=2` enables the experimental parallel path. Independent
PDFium instances load independent page handles, then take tiles from a shared
atomic work index. Callbacks are serialized, result ordering is preserved,
and the viewer commits one completed composite. No native handle is shared
between PDFium instances. Print/export continue using their existing batch.

## Measurements

Two supplied files, page 1, full page raster width 6000 pixels, a centered
1920x1080 viewport, 640-pixel tiles with existing gutters. Each configuration
ran in a fresh process with a two-instance pool: one cold-page render then
three warm-page renders. Values below are preliminary warm medians, including
tile composition where applicable. These are offscreen native rendering
measurements, not GUI frame/input latency. Files stayed on T: through the
existing block cache; the OS/network caches were not flushed between runs.
Private memory is a process snapshot, not peak usage or production app RAM.

| File | Serial tiles | Two workers | Single crop | Approx. private MiB (serial / two / crop) |
| --- | ---: | ---: | ---: | ---: |
| Drainage page 1 | 590 ms | 361 ms | 437 ms | 423 / 803 / 413 |
| BinhDo page 1 | 144 ms | 94 ms | 98 ms | 148 / 257 / 140 |

Two workers improve throughput by about 39% / 34% versus serial tiles, but
duplicate heavy parsed pages. Single-crop rendering improves the same
baseline by about 26% / 32% with lower process memory, so it is the balanced
default. The parallel mode improves drainage by about 17% versus one crop
but costs about 390 MiB more in this probe.

Cold-page completion (serial / two / crop): drainage 1311 / 1051 / 1125 ms;
BinhDo 392 / 400 / 399 ms. Parallelism does not guarantee faster page opening.
First warm tile was not faster with two workers on drainage (about 122 ms
versus 101 ms); a faster complete batch is not a faster initial response.

Serial and parallel tile composites had identical SHA256 pixel hashes on
both supplied pages. Single-crop hashes differ from tiled hashes; cross-mode
pixel equality is not claimed because clipping/antialias rounding can differ.
Each mode was nonblank and stable across repeated renders.

## Verification and reproduction

The native worker checks cover pixel equality, delivery exactly once, result
ordering, separate page handles, cancellation, load cleanup, subsequent reuse,
and concurrent batches. The existing scroll-quality suite covers cache reuse,
handoff and pixel quality. These tests do not validate continuous zoom latency
or multi-tab memory under the experimental mode.

Build `Tests/PerformanceTests.csproj`, then run:

```powershell
dotnet run --project Tests/PerformanceTests.csproj --no-build -- --viewport-workers-check
dotnet run --project Tests/PerformanceTests.csproj --no-build -- --scroll-quality
# Workers: 0 = single crop; 1 = serial tiles; 2 = parallel tiles.
dotnet run --project Tests/PerformanceTests.csproj --no-build -- --viewport-workers-bench 'PATH.pdf' 1 2
```

For a reader A/B test set `XTPDF_VIEWPORT_WORKERS=2` before launching the app.
Unset it (or set 1) for the balanced single-crop default. The instance pool
must contain at least two native DLL instances; the existing build copies them.

## Foxit evidence still needed

This change addresses measured costs in our renderer. It is not proof that
Foxit has the same scheduler. The runtime study and Capture-FoxitStacks.ps1
describe the missing elevated CPU-stack capture. Use engine stack overlap to
identify concurrent native work, compare revisit work to initial work, and
correlate graphics calls (bitmap copy/stretch/paint) with rendering. Private
symbols may be absent, so offsets and API context must support any conclusion.
CPU stacks alone cannot prove exact tile size or identify every cache layer.
