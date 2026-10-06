# RAM reclamation experiments — 2026-10-06

Goal: recover as much memory as practical while retaining the accepted reader
presentation. Nine experiment variants, 20 fresh test-process runs; 439
assertions passed. Experiments run through `--reclaim-memory-profile`, not
automatically in the production reader. Existing adaptive defaults are retained.
The only additional runtime primitive is gated retirement of idle worker lanes;
it is invoked by the experimental harness, not by normal startup.

Additional validation: 38 adaptive regression checks and 20 batch-runner smoke
checks passed. The runner smoke used a generated simple PDF and is excluded
from the 20 measured drawing trials above. Its background collection reduced
managed heap but did not reduce combined private commit after one second
(402.8 to 406.5MiB), so reclamation gains are workload-dependent.

## Workload and method

- Local 173,799,414-byte `03. QUYEN 2.2 - TNM, TNT, HKT, CAY XANH, CHIEU SANG,
  TCTC.pdf`: focus page 31, plus pages 1, 2, 31, 32.
- Local 20,599,234-byte `XT Sheet Set2.pdf`: first four pages and background previews.
- Offscreen nonactivating WPF viewport 1320x700. First screen retains a real
  4608px page bitmap. Initial seven runs retain one visible page; the remaining
  13 runs also retain the sharp next-page bitmap, matching nearby prefetch.
- Each process first builds the same warm workload, then invokes the existing
  critical adaptive policy and waits 10.2 seconds for idle background retirement.
  This provides a reference with two foreground workers and a 256MiB aggregate
  retention target. Pressure is injected, not induced by consuming OS free RAM.
- Experiment follows; measure parent private commit/working set and worker
  private commit after one quiet second. Then revisit the visible image, nearby
  zooms, first uncached deep-zoom crop and pan. Neighbor runs also scroll to the
  next page and measure its first deep-zoom crop.
- Memory comparisons use combined parent/worker **private commit**, not physical
  RAM usage. Parent working set is recorded separately. Raw deltas are paired
  within each process; OS caches remain naturally warm. Source hashes are streamed.
- Every run asserts frame pixels remain identical after reclamation, visible and
  retained neighbor images remain cache hits, nearby zoom reuses the sharp bitmap,
  deep zoom/pan produce regions, and both source PDF SHA-256 hashes stay unchanged.
- Four principal neighbor variants were repeated three times in varied order.
  No user window was activated or closed, no working-set purge was used, no
  source file was edited, and no real machine-memory shortage was induced.

## Single-page screening: one run per variant

All start with about 775–778MiB combined private commit after adaptive trimming.

| Experiment | Private after MiB | Additional private released MiB | First deep crop ms |
| --- | ---: | ---: | ---: |
| Existing adaptive reference | 784.4 | -7.4 | 57.8 |
| Drop unprotected caches only | 783.5 | -6.8 | 75.8 |
| Cache drop + two blocking full collections/finalizer drain | 504.2 | 274.1 | 89.4 |
| Cache drop + one background full collection | 503.7 | 272.1 | 47.8 |
| Cache drop + keep one foreground worker + blocking collection | 446.1 | 330.4 | 77.6 |
| Cache drop + park all workers + blocking collection | 380.5 | 394.3 | 265.4 |
| Cache drop + blocking collection + native heap compaction | 505.7 | 272.8 | 44.6 |

Cache ownership fell from 86.6 to 43.2MiB when only the displayed bitmap remained,
but private commit did not fall without collection. The small increase in the
reference/cache-only rows includes observation/WPF work. Explicit collection
allowed abandoned bitmap wrappers/buffers to be finalized/collected. This does
not prove every retained private allocation is a leak. Native heap compaction
showed no additional useful reclamation in this screening.

## Current + neighbor: medians of three independent runs

| Experiment | Private after MiB | Paired release MiB | Deep zoom current ms | Deep zoom neighbor ms | Foreground workers |
| --- | ---: | ---: | ---: | ---: | ---: |
| Existing adaptive reference | 783.0 | -6.8 | 41.6 | 86.7 | 2 |
| Existing caches + background collection | 545.4 | 230.4 | 44.8 | 94.8 | 2 |
| Keep one foreground worker + background collection | 495.2 | 285.9 | 62.2 | 248.9 | 1 |
| Park all workers + blocking collection | 425.7 | 350.6 | 260.9 | 239.3 | 0 |

Keeping only live caches plus background collection was also screened once with
the neighbor retained: 547.3MiB after, current/neighbor crops 42.5/87.6ms. It
removed only ~0.47MiB beyond the existing adaptive cache target in this workload;
the two protected 4608px pages already account for ~86.4MiB. It offered little
benefit over collecting with the existing adaptive budgets.

Background collection with two warm foreground workers released 228–274MiB
within the paired runs. Visible and neighbor whole-page revisits remained below
1ms. Current deep crops measured 36.8–76.0ms and neighbor crops 68.1–95.5ms.
Parking the spare worker saved more native commit but the neighbor crop needed
233.8–259.3ms. Parking everything also made the current crop take about 261ms.
Whole-page zoom stays sharp while waiting, but those delays can be felt when
the user resumes deep zoom or pans beyond retained crop coverage.

The synthetic 16ms dispatcher heartbeat had median maximum gaps 32.9ms for the
reference, 39.8ms for background collection, 37.2ms when parking one worker and
50.5ms with blocking collection/parking all. This is a coarse offscreen scheduling
observation, not display FPS or an input-latency benchmark. Background GC can
still pause managed threads. Tests request it only in a quiet phase.

## Selection

Best evidence for preserving current responsiveness: **retain the existing
protected cache/foreground workers and request a background Gen2 collection
after substantial cache release, only during sustained idle**. No additional
drop of live/neighbor images is justified by these results. Blocking collection
and native heap compaction did not establish a better default.

Maximum reclamation mode: parking every worker, but its cold-start penalty makes
it suitable for explicit sleep/very long inactivity, rather than ordinary pauses
between zoom/pan. Parking one worker similarly needs a longer idle threshold or
an explicit memory-saving mode because the neighbor's first crop slows down.

The experimental modes are not automatically enabled. Before integrating idle
collection, add a substantial-eviction/allocation trigger, a sustained quiet
period, rate limiting, cancellation on new interaction and diagnostics. Never
run collection on every two-second policy tick. Actual interactive validation
and the original supplied drainage/BinhDo pair remain additional calibration.
Memory rises again when new crops are rendered; reclamation is not a hard cap.

## Reproduce

```powershell
./Tests/Run-ReclaimMemoryExperiments.ps1 -FirstPdf '<PDF with pages 31/32>' -SecondPdf '<PDF with at least 4 pages>'
# Smaller one-visible-page screening; select additional modes as needed:
./Tests/Run-ReclaimMemoryExperiments.ps1 -FirstPdf '<first PDF>' -SecondPdf '<second PDF>' -PageNumber 1 -SinglePage -Repeats 1 -Modes reference,cache-only,cache-gc,cache-background-gc,park-spare-gc,park-all-gc,cache-gc-heap
```

Raw run JSONs are ignored test output:
`Tests/bin/Release/net10.0-windows10.0.19041.0/results/reclaim-*-page-31-*.json`.
`reclaim-summary.json` records all 20 samples. The runner writes a separate batch
summary and rotates trial order. Build passed; existing CS8602 test warning
remains in `UiSmokeTests.cs:117`. The retained baseline reader is the Release
output; experimental switches are accepted by the test DLL only.
