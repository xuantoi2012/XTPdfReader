# Foxit Comparison and Warm Zoom Cache, 2026-10-02

## Desktop Observations

Computer Use was explicitly authorized on monitor 1. Both viewers were observed
there, using QUYEN 2.2 (162.08 MiB, 276 pages), including page 11. No PDF was edited
or saved. The pre-existing signed Foxit document was left intact in its own tab.

Foxit retained ready page imagery and thumbnails when revisiting the observed
area. This is consistent with image/native-state reuse, but observation does not
establish whether Foxit stores a particular cache in RAM or on disk. Its private
cache implementation and storage location were not inspected.

The Reader's old 54% -> 100% transition visibly presented a white page before
refinement. The draw-time quality filter rejected the previously rendered small
page as too coarse for the enlarged viewport. This was a continuity regression,
not proof that the PDFium instance count was wrong.

After the change, the same transition retained the drawing, temporarily enlarged,
then sharpened. A slider transition to 330%, return to 100%, and Start -> document
return also retained imagery. Previously seen sidebar thumbnails remained ready.
Screenshots are point-in-time observations, not frame-time or FPS measurements.

## Selected Change

- Keep an already-rendered page image of at least 512px during zoom refinement.
  Do not enlarge a cold 340px sidebar thumbnail into the reading viewport.
- Rehydrate page state from the weak placement image or bounded reader cache.
- A larger cached full-page image can satisfy a lower-resolution request for
  the same source, page and layer token. It cannot satisfy a sharper request.
- Retain sharp viewport regions in a shared 16 MiB LRU, allowing reuse after a
  normal/deep zoom or document rebind. Oversized crops are displayed without
  entering this cache; older entries are evicted by bytes.
- Invalidate retained regions on source-page edits, geometry/layer changes, and
  source close. A generation guard prevents invalidated in-flight work from
  restoring an obsolete cache entry.
- Report retained and live region memory in Debug without double-counting shared
  bitmap references.

Keep the earlier balanced settings: two PDFium instances, four-page directional
prefetch, 64 MiB full-page cache, 48 MiB thumbnail cache, four idle native page
handles per instance, two warm files, and viewport-first deep rendering.
The additional 16 MiB budget is not a whole-process RAM limit. Visible images,
in-flight renders, PDFium and WPF consume other memory. No persistent disk image
cache or user registry setting was added.

## Repeated Heavy-File Measurement

Six separate Release processes ran in alternating off/on order, three each.
"Off" disables only this warm-image policy, not the earlier performance changes.
The real continuous viewer used a non-activating, offscreen 1263 x 792 DIP
viewport, DPI 1, default two-instance pool and four-page prefetch. The source file
buffer completed before timing. OS/file caches were not flushed.

Page 11 was viewed at 50%, 100%, 125%, then 100%; that sequence was repeated three
times. Three normal/deep document rebinds followed the initial 300% view, then
25 seconds of ordinary idle reclamation. Per-stage diagnostics include queued
prefetch, so they are not isolated costs for just the sampled page.

| Metric | Warm Policy Off | Warm Policy On |
| --- | ---: | ---: |
| Sum of nine warm zoom readiness waits, median per run | 528.76 ms | 2.59 ms |
| Peak private memory, median | 419.55 MiB | 391.71 MiB |
| Private memory after 25s idle, median | 373.82 MiB | 324.92 MiB |
| Active CPU, median | 10.969 s | 10.391 s |
| Retained full-page cache at end, median | 59.21 MiB | 44.37 MiB |
| Retained viewport cache at end | 0 | 6.56 MiB |
| Warm deep-rebind readiness, median of nine samples | 28.97 ms | 29.96 ms |
| Warm deep-rebind readiness, worst observed | 712.31 ms | 31.38 ms |
| Completed bitmap copies in warm deep-rebind stages, sum | 17 | 0 |

All runs finished, source sizes/mtimes were unchanged, and all 138 same-step
sampled bitmap hashes matched across the six runs. Different pixel widths after
different steps are compared by step index, not merely by zoom percentage.

The sub-millisecond warm zoom numbers mean the required cached image was already
available; they do not mean it reached the display in that time. Deep rebinds
still wait for WPF's request/layout tick. Their median did not improve, although
the new crop cache removed repeated bitmap creation and the observed long waits.
Peak memory varied: off 415.84-427.08 MiB, on 380.24-426.28 MiB. Three trials do
not establish a universal RAM or CPU improvement. Cold native page work remains.

The live Reader desktop session, with thumbnails and a different viewport,
showed approximately 424-432 MiB private memory, 46 MiB reader images, 4 MiB
retained regions and 5 MiB thumbnails. Do not compare this directly with the
offscreen table or Foxit's unmatched window/state. Foxit was not used as a
controlled RAM/FPS baseline.

## Verification and Reproduction

Release build: zero warnings/errors. The background regression suite passed
267 checks in three consecutive final runs. Foreground dialog/recovery test
groups were excluded. New checks cover delayed-refinement non-white pixels,
larger-image lookup, layer separation, weak-image rehydration, actual region
reference reuse without native rasterization, edit invalidation and close purge.
Scroll tests now use a fixed travel distance rather than assuming how many
dispatcher ticks occur in 180ms.

```powershell
dotnet build Tests/PerformanceTests.csproj -c Release
dotnet run --project Tests/PerformanceTests.csproj -c Release --no-build -- --background-regression
dotnet run --project Tests/PerformanceTests.csproj -c Release --no-build -- --warm-cache-profile off false --profile-sources '<source-manifest.json>'
dotnet run --project Tests/PerformanceTests.csproj -c Release --no-build -- --warm-cache-profile on true --profile-sources '<source-manifest.json>'
```

The manifest uses the same RecentFile array as the heavy tuning harness; this
focused profile selects its last file. Local raw results are under
`Tests/bin/Release/net10.0-windows10.0.19041.0/results/warm-cache-{off,on}-{1,2,3}.json`.
The benchmark does not change recents or settings and clears its retained caches
at shutdown. No source PDF is rewritten.

This supplements [the earlier cold/deep tuning report](READER-TUNING-2026-10-02.md).
It fixes an observed continuity problem, but is not a claim that every zoom and
scroll is now smooth at a particular refresh rate. Continuous frame-time capture
would be needed for that conclusion.
