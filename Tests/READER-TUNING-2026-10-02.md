# Reader Tuning, 2026-10-02

Follow-up: [Foxit comparison and warm zoom caching](FOXIT-ZOOM-CACHE-2026-10-02.md)
addresses a subsequently observed white flash during zoom and warm revisit reuse.
The measurements below predate that change and are not a display-FPS conclusion.

## Selected Policy

- Two PDFium instances by default (one on low-core machines).
- Four-page directional prefetch; 64 MiB reader bitmap cache.
- Four idle native page handles per instance; normal hot-page protection.
- Two warm files. Existing 15-second stale-document and 20/90-second idle rules remain.
- Original page-affinity score (missing-page penalty 3); no expanded native prefetch retention.
- At deep zoom, a readable 1024px fallback plus a sharp viewport region, without
  redundantly upgrading the whole page and every prefetched page to 2304px.
- Start suspends viewer requests and nearby thumbnail warm-up, releases native
  hot-page protection, and resumes rendering when returning to the document.

No user registry values were changed. The user's existing warm-file setting is two.
This complements [the pool-size decision](POOL-BALANCE-2026-10-02.md).

## Default Confirmation

The no-override profile confirmed the actual defaults: two instances, prefetch
four, reader cache 64 MiB, native cache four, two warm files, affinity three,
and viewport-first enabled. All normal sampled pages and the post-idle page
became ready; buffers completed and source lengths/mtimes stayed unchanged.

An additional cold page 81 was bound directly at 300% without a prior page
bitmap. This exposed an existing mixed-paper-size binding bug: horizontal offset
zero placed the page entirely beyond the right viewport edge, so no visible
region could be requested. New document bindings now start horizontally centered;
normal user pan offsets are not continuously reset. The rerun produced a 1024px
fallback and a fully covering sharp region in 343.4 ms. This is one correctness
check, not a comparative latency median.

Release build passed with zero warnings/errors; the background regression suite
passed 246 checks, including covered-view suspension/resume, normal/deep resolution
policy, and mixed-size cold binding. Foreground dialog/recovery tests remain
excluded while the user works.

## Screening

Seven configurations were run twice in opposite orders on the two heavy PDFs,
excluding a warm-up. Values are medians; with two observations they are the
average of the two middle values, not evidence of universal performance.

| Variant | Sampled readiness ms | Peak private MiB | After idle MiB | Repeated native parses |
| --- | ---: | ---: | ---: | ---: |
| Current policy | 1685.2 | 884.4 | 574.2 | 36.0 |
| Two-page prefetch | 2296.6 | 831.1 | 488.8 | 12.0 |
| Reader cache 32 MiB | 2424.5 | 887.2 | 501.8 | 42.0 |
| Reader cache 96 MiB | 1687.8 | 887.0 | 562.8 | 34.0 |
| Native page cache 8 | 1833.4 | 971.8 | 530.3 | 36.5 |
| Retain native prefetch pages | 1835.4 | 879.3 | 536.2 | 24.5 |
| One warm file | 2814.4 | 673.8 | 285.7 | 34.0 |

Shrinking the bitmap cache did not reliably shrink total private memory, because
native state and extra re-renders dominate. One warm file saves memory but causes
large page-delay regressions; more native page handles add memory without a clear
benefit. Two-page prefetch trades fewer native parses for more visible waits.

## Four-File Validation

The source manifest contains Drawing-16 (19.4 MiB, 73 pages), a three-page text
PDF (0.5 MiB), QUYEN 2.1 (172.7 MiB, 332 pages), and QUYEN 2.2
(162.1 MiB, 276 pages): 684 pages in total. All source files were read-only.

Three variants were repeated three times: current policy, expanded native
prefetch retention, and retention plus a missing-page affinity penalty of nine.
Each performs identical page visits, 100 scroll steps of 65 DIPs per file,
100% zoom, two 125%/100% zoom cycles, 300% viewport rendering, pan, return to
the first document, 25 seconds idle, and a previously unvisited post-idle page.

Expanded retention reduced median repeated parses from 70 to 28, but did not
improve the balanced outcome: peak memory 944.9 -> 970.9 MiB, sampled waits
3884.7 -> 4001.7 ms, and second-heavy-file missing scroll ticks 35 -> 39/100.
Stronger global affinity improved some zooms, but raised missing scroll ticks
to 46/100. These variants remain available for comparisons, not production defaults.

The initial pan of 200/120 DIPs was often inside the retained region margin.
Its near-zero latency is valid cache reuse, not a fresh-region benchmark.
The final experiment below uses 600/400 DIPs to exercise a new crop.

## Viewport-First Rendering

Code inspection identified redundant deep-zoom work: the viewer requested a
2304px full page and a viewport region even with an existing readable image.
The selected change keeps screen-resolution full pages at normal zoom, but uses
a 1024px fallback at zoom requiring a region. The viewport region's effective
resolution, clipping and pixels are unchanged.

The old and new policies were repeated three times each on the same four files,
excluding a warm-up. Both use identical settings and the larger pan.

| Metric, median of three | Old policy | Viewport-first |
| --- | ---: | ---: |
| Sampled readiness waits, all files ms | 4121.7 | 3360.4 |
| Peak private MiB | 963.9 | 879.1 |
| After 25 seconds idle MiB | 313.2 | 317.7 |
| Active CPU seconds | 21.64 | 20.81 |
| Whole scenario seconds | 23.933 | 24.517 |
| QUYEN 2.2 page 11: 300% zoom + pan ms | 999.4 | 185.0 |
| New post-idle page ms | 166.2 | 156.6 |
| QUYEN 2.2 missing scroll ticks /100 | 40 | 35 |
| Active dispatcher delay P95 ms | 21.7 | 22.3 |

Readiness waits fell 18.5%, sampled peak private memory 8.8%, and that heavy-page
zoom-plus-pan sequence 81.5%. The combined zoom/pan values are medians of the
per-run sums, not sums of independent medians: old range 991.4-1030.9 ms,
new 166.0-210.3 ms. CPU improves modestly; idle memory is essentially unchanged.
The whole-scenario duration includes fixed pauses and scheduling variation and
does not show a speedup. Do not present the latency win as a universal throughput win.

100% page bitmap hashes, 300% region hashes, and pan-region hashes matched across
all six runs for every source file. All sampled pages became ready, source buffers
finished before interaction timing, and source lengths/mtimes were unchanged.

### Final Strict Comparison

After correcting cold binding, the old/new policies were repeated three times
each again, excluding another warm-up. Both policies received the same binding
fix. The region waiter now also requires the image to cover the entire currently
visible page area; merely retaining an image at the right resolution is not enough.
These are the final confirmation numbers, superseding the initial comparison's
headline percentages above.

| Metric, median of three | Old policy | Viewport-first |
| --- | ---: | ---: |
| Sampled readiness waits, all files ms | 4627.2 | 3274.4 |
| Peak private MiB | 954.3 | 894.6 |
| After 25 seconds idle MiB | 302.9 | 308.6 |
| Active CPU seconds | 22.77 | 21.58 |
| Total process CPU seconds, including open/idle/resume | 26.20 | 27.92 |
| Whole scenario seconds | 24.507 | 24.419 |
| QUYEN 2.2 page 11: 300% zoom + pan ms | 985.8 | 179.8 |
| New post-idle page ms | 163.2 | 152.4 |
| QUYEN 2.2 missing scroll ticks /100 | 44 | 32 |
| Active dispatcher delay P95 ms | 21.6 | 30.4 |

Sampled readiness waits decreased 29.2%, peak private memory 6.3%, and the
heavy-page zoom/pan sequence 81.8%. Its per-run sums were 955.8/985.8/1068.3 ms
versus 169.1/179.8/211.1 ms. All six runs completed and matched every sampled
normal/deep/pan bitmap hash with source files unchanged.

This is a balanced latency/memory decision, not an across-the-board improvement:
idle memory is effectively unchanged, total process CPU was higher despite lower
active CPU, and the dispatcher P95 did not improve reliably (the initial batch
was 21.7/22.3 ms). Repeated native parses also remain, median 67/76 in this batch.
Physical-display frame pacing and the full shell still need a later interactive
check when desktop testing is appropriate.

## Diagnostics And Limits

The benchmark now records active-phase timing counts/totals, native parse reuse
across instances, cancelled requests, native cache hits, GC collections/pause
duration, CPU, managed allocation and policy trims. An optional native-parse
observer is attached only by tests; ordinary use retains no per-page audit map.
Timing totals are accumulated across concurrent requests; they are not additive
critical-path durations or percentages of wall time.

In the final comparison, GC pause medians were 91.5/101.0 ms over roughly 24 seconds
of active work. They do not dominate this offscreen scenario. Native rasterization
and repeated parsing remain real costs; viewport-first is not a claim to eliminate
all wasted work or every possible bottleneck.

There are 35 measured comparison runs in this tuning session (14 screening,
9 reuse-policy validation, 6 initial and 6 strict viewport comparisons), plus four excluded
warm-ups. Processes run BelowNormal in a non-activating offscreen WPF host.
No Computer Use, desktop input, recents/recovery writes or PDF edits are performed.
The user continued other work; OS caches were not flushed. These measurements
are not physical display FPS or a certification of full-shell multi-tab frame pacing.

## Reproduction

```powershell
dotnet build Tests/PerformanceTests.csproj -c Release
./Tests/Run-ReaderTuningComparison.ps1 -Rounds 2
./Tests/Run-ReaderTuningComparison.ps1 -Rounds 3 -Cohort reader-viewport-repeat -SourceManifest Tests/bin/Release/net10.0-windows10.0.19041.0/results/reader-validation-2026-10-02-sources.json -Variants baseline,viewport-first -ZoomCycles 2 -DeepZoom
dotnet run --project Tests/PerformanceTests.csproj -c Release --no-build -- --background-regression
dotnet run --project Tests/PerformanceTests.csproj -c Release --no-build -- --background-heavy-profile selected-default-repeat 4 --profile-sources Tests/bin/Release/net10.0-windows10.0.19041.0/results/reader-validation-2026-10-02-sources.json --cold-deep-check
```

Evidence is under `Tests/bin/Release/net10.0-windows10.0.19041.0/results/`:
`reader-tuning-2026-10-02-summary.json`, `reader-validation-2026-10-02-summary.json`,
`reader-viewport-2026-10-02-summary.json`, `reader-viewport-strict-2026-10-02-summary.json`,
`heavy-background-selected-default-final.json`, their per-run JSON, source manifests,
and option files. The runner restores its process-local pool override on exit.
