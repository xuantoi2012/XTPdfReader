# PDFium Pool Balance, 2026-10-02

## Decision

Use two PDFium instances by default (one on low-core machines), keeping four-page
prefetch, the 64 MiB reader cache, and the current idle reclamation policy.
Keep the explicit `XTPDF_PDFIUM_INSTANCES` override for controlled comparisons.
The production build still includes four DLLs; unused copies are not loaded.

This supersedes the preliminary four-instance recommendation in
[the initial heavy-file report](HEAVY-FILES-2026-10-02.md).

## Repeated Measurements

Two real CAD PDFs from the recent-file list were opened read-only:
QUYEN 2.1 (172.7 MiB, 332 pages) and QUYEN 2.2 (162.1 MiB, 276 pages).
The machine has 28 logical processors. One four-instance warm-up was excluded,
followed by three rounds with orders 2/4/6/3, 6/3/2/4, and 3/6/4/2.
Every run confirmed the actual loaded DLL count, including six-instance runs.

All rows below are medians of three separate processes. Peak memory is sampled
on dispatcher ticks. Memory is private MiB, not working set or a whole-system RAM limit.

| Instances | Active scenario seconds (min-max) | Peak MiB | After idle MiB | CPU seconds | Heavy-page zoom ms | Post-idle new page ms |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 12.539 (12.478-12.628) | 857.5 | 521.2 | 20.89 | 485.5 | 157.5 |
| 3 | 12.532 (12.401-13.382) | 957.7 | 591.8 | 23.62 | 620.4 | 188.9 |
| 4 | 12.883 (12.821-12.967) | 1049.0 | 616.7 | 23.03 | 619.9 | 183.1 |
| 6 | 12.337 (12.269-12.660) | 1167.2 | 648.5 | 24.16 | 394.0 | 123.8 |

Each file receives the same page visits, exactly 100 scroll steps of 65 DIPs,
and page 11 at 100% zoom. The scenario includes deliberately fixed pauses and
is not a pure raster-throughput benchmark. Summed sampled readiness waits were
1703.5/1902.4/1791.8/1337.7 ms for 2/3/4/6 respectively. Six instances do help
some individual operations; the modest whole-scenario difference must not hide
that latency improvement.

On the second file, median ticks with at least one visible page lacking its
full bitmap were 25/33/27/27 out of 100. Active dispatcher delay P95 medians
were 23.6/18.8/19.8/18.2 ms beyond a nominal 16 ms timer. These are backend
readiness and dispatcher proxies, not physical display FPS. Two instances do
not worsen sampled scroll readiness, although dispatcher delay is slightly higher.

Compared with four instances, two save 191.5 MiB (18.3%) peak private memory,
95.5 MiB (15.5%) after idle, and 9.3% measured CPU time. Compared with two,
six use 309.7 MiB (36.1%) more peak memory for a 1.6% shorter active scenario;
their heavy-page zoom is 18.8% faster and post-idle page is 21.4% faster.
For the user's many-tab reading workload, two is the balanced default, not a
claim that two is universally the fastest configuration.

## Controls And Limits

- Sources were frozen in a manifest; file lengths and modification times remained unchanged.
- Source buffering finished before timing interactions; OS caches were not flushed.
- All sampled pages, zooms, scroll destinations and post-idle pages became ready.
- The two 100% zoom bitmap hashes matched across all 12 measured runs.
- Bitmap resolution was not reduced. Prefetch stayed at four for every run.
- The real continuous viewer ran offscreen, never activated, at BelowNormal priority.
- No Computer Use, desktop input, registry writes, recents writes or source edits were used.
- The user continued other work. Three repeats and two files do not establish universal
  performance, long-term leak freedom, or whole-shell multi-tab frame pacing.

## Reproduction

```powershell
dotnet build Tests/PerformanceTests.csproj -c Release
./Tests/Run-HeavyPoolComparison.ps1 -Rounds 3
dotnet run --project Tests/PerformanceTests.csproj -c Release --no-build -- --background-regression
```

Evidence is under `Tests/bin/Release/net10.0-windows10.0.19041.0/results/`:
`pool-fixed-2026-10-02-summary.json`, `pool-fixed-2026-10-02-sources.json`,
and `heavy-background-pool-fixed-2026-10-02-r*-k*.json`.
The background regression mode omits foreground dialog and recovery-window tests.

Release build completed with zero warnings/errors. The background regression suite
passed 232 checks, including the new default/override policy and existing
rendering, cancellation, cache, save-safety, and offscreen scroll-quality checks.
An additional run without an instance override confirmed two loaded instances,
four-page prefetch, 870.1 MiB sampled peak and 517.0 MiB after idle. All readiness,
buffer and source checks passed; zoom hashes matched the repeated comparison.
Its evidence is `heavy-background-balanced-default-confirmed.json`.
