# Background cache and native-retention follow-up, 2026-10-02

## Decision

Keep WPF and the two-instance PDFium rendering pool. Add a **16 MiB recent-page
display-preview cache** alongside the existing 64 MiB sharp-page cache. After
20 seconds without native work, keep one warm document replica for the hot PDF,
prioritizing the explicitly focused page. Do not close busy leases or force GC
on zoom/scroll. Keep the existing long-idle and inactive-file policies.

This improves bounded warm-page display and idle memory. It does not establish
Foxit-equivalent display FPS, cold-page latency, or peak memory.

## Changes

- Downsample completed page pixels to at most 512x1024, materialize an independent
  frozen bitmap, and store it in a separate bounded LRU. A lazy TransformedBitmap
  is not retained, since it would also retain the full-resolution source.
- A preview is display-only. Sharp requests still require the requested native
  resolution; page/source/layer invalidation and source closure remove both tiers.
- Hydrating a page from cache now invalidates the drawing surface immediately.
  A new software-composition pixel test caught the previous behavior: the state
  had an image but the frame remained white until another repaint/refinement.
- Native ownership selection prioritizes the focused page, then hot pages that
  served foreground rendering, other hot handles, recency, and a stable tie break.
- Idle-only retirement atomically checks active lease users and removes the
  exact cache entry. Queued operations and resumed native work abort retirement.
  An acquisition that raced this idle retirement can safely retry; explicit
  source closure/editing retains the existing cancellation behavior.
- Debug memory totals include preview bytes. Cache budgets are not process RAM
  limits; visible images, in-flight buffers, native objects and WPF use more.

## Repeated Background Comparison

Final cohort: `results/retention-focus-summary.json`, nine separate processes,
three rotated rounds of baseline, preview-only and balanced. All processes run
below-normal priority; WPF hosts are offscreen and non-activating. No Computer
Use, desktop input, user settings, recents, or source writes.

Source: the frozen manifest's 276-page, 162.08 MiB CAD PDF. Viewport: 1280x800
host at the recorded machine DPI. Each process visits pages 1, 11, 21, ..., 81,
forces the same sharp-cache eviction for page 51, revisits it, returns to page
11, populates its thumbnail replica, idles 25 seconds, then resumes at 125%,
300%, and 100%. Source buffering completes before interaction measurements;
OS caches are not flushed. The eviction probe explicitly discards the test
placement's weak image and rebinds the viewer, so it is a controlled cache test.

| Median metric | Baseline | Preview Only | Balanced |
| --- | ---: | ---: | ---: |
| Sampled peak private MiB | 575.39 | 597.94 | 580.63 |
| Private MiB after 25 s idle | 536.49 | 559.60 | 456.36 |
| CPU seconds, measured workload | 11.250 | 10.781 | 11.625 |
| Cold-page readiness sum, ms | 1523.97 | 1553.81 | 1526.75 |
| Evicted revisit final sharp image, ms | 49.30 | 51.62 | 59.81 |
| First sharper zoom after idle, ms | 435.32 | 462.77 | 425.27 |
| Deep-zoom region after idle, ms | 100.50 | 99.87 | 107.07 |
| Return to cached normal zoom, ms | 0.14 | 0.28 | 0.21 |
| Dispatcher timer delay p95, ms | 19.55 | 20.17 | 23.46 |
| Native documents after idle | 2 | 2 | 1 |
| Immediate viewer-state fallback, runs | 0/3 | 3/3 | 3/3 |

Balanced idle private memory falls **14.9% (80.13 MiB)** versus baseline. Its
sampled peak is not meaningfully lower. Cold readiness and first sharper resume
remain similar; final revisit sharpening is not faster. CPU and dispatcher-delay
figures do not demonstrate a general speedup. The useful display change is that
previously viewed content can be shown before sharp refinement, within the
preview LRU's capacity. Previews may look soft temporarily and do not cover every
page of a long document indefinitely.

All 126 final sharp page/crop samples match dimensions and SHA-256 hashes across
the nine runs. Every run reports all stages ready and unchanged source length
and last-write time. These are render/cache-ready measurements, not on-screen
presentation latency or FPS. The separate WPF pixel regression verifies preview
painting; it is not a GPU/display benchmark.

An earlier non-focus-aware consolidation cohort (`retention-final-summary.json`)
reduced idle memory but raised median first sharper resume from 405.00 to 619.49
ms. That policy was refined rather than selected. Exploratory cohorts with a
natural eviction/weak-row readiness mismatch were rejected and are not included
in the final medians.

The previous [Foxit desktop measurements](FOXIT-DESKTOP-COMPARISON-2026-10-02.md)
use a different GUI workload. Do not compare this offscreen table directly with
Foxit's GUI RAM figures or infer Foxit's internal cache architecture from it.

## Verification And Reproduction

Release build: zero warnings/errors. Background regression: 288 checks passed,
including preview budget/LRU, layer/source invalidation, sharp refinement,
actual preview pixels, focused native ownership, active/queued lease protection,
resumed-work protection, retirement and safe reopening.

The final suite passed twice consecutively after the four-file smoke profile
(and once immediately before the repeated comparison). The four-file profile
`heavy-background-retention-four-files.json` uses the frozen four-source cohort,
including both heavy CAD PDFs, with production prefetch=2 and preview budget=16
MiB. All pages/zoom/resume stages and source checks passed: sampled active peak
884.9 MiB, after-idle private 293.6 MiB, native documents 4 -> 2, return to the
first document 34.1 ms, and a new page after idle 167.5 ms. This is one validation
run, not a repeated before/after memory comparison; its different workload must
not be mixed into the single-PDF medians above.

```powershell
dotnet build Tests/PerformanceTests.csproj -c Release
./Tests/Run-ReaderRetentionComparison.ps1 -Rounds 3 -Cohort retention-recheck -SourceManifest Tests/bin/Release/net10.0-windows10.0.19041.0/results/reader-validation-2026-10-02-sources.json
dotnet Tests/bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.PerformanceTests.dll --background-regression
```

`XTPDF_HOT_DOCUMENT_COPIES=0` reproduces the previous hot-file native retention;
the default is 1. A preview-budget argument of 0 reproduces full-page-only image
caching in the retention profiler. The comparison script sets these only for
its child processes and restores its process environment afterward.

Next evidence needed: a matched authorized GUI frame-pacing comparison and a
longer continuously active scrolling workload. Current native consolidation
acts after idle, so it cannot solve every active-session memory peak.
