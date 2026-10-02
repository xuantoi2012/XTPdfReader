# Heavy PDF Background Measurements, 2026-10-02

These preliminary single-run comparisons are superseded by the
[repeated fixed-work pool comparison](POOL-BALANCE-2026-10-02.md).
The final production decision is two instances with four-page prefetch.

## Scope

Two recent read-only source PDFs on drive P: were profiled:

- QUYEN 2.1: 172.7 MiB, 332 pages.
- QUYEN 2.2: 162.1 MiB, 276 pages.

The running desktop app was initially observed at 1,081 MiB private memory and
870 MiB working set. It exited before continuous sampling began, so that sample
must not be treated as a full live-session trace.

The following runs use the real ContinuousPdfView, PDFium pool, 64 MiB reader
cache, source block cache, and normal memory policy in a separate process.
The WPF host is offscreen and never activated; process priority is BelowNormal.
There is no Computer Use, mouse/keyboard injection, source editing, annotation
restoration, recovery writes, registry writes, or recent-file writes.
Source lengths and modification times were unchanged after every run.

Each run reads page sizes, visits pages 1, 2, 6, 11 and the midpoint in each file,
simulates 2.5 seconds of scrolling from page 21, returns to the first document,
and observes 25 seconds of idle reclamation. The zoom runs additionally render
page 11 of both documents at 100%, checking bitmap resolution before declaring
the page ready. The machine has 28 logical processors.

## Fit-Width Runs

| PDFium instances | Pages prefetched | Peak private MiB | Before idle MiB | After idle MiB | CPU seconds | Return to first tab ms |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 4 | 4 | 1052.9 | 986.9 | 557.4 | 21.41 | 16.1 |
| 4 | 2 | 1013.0 | 930.6 | 612.7 | 17.14 | 24.6 |
| 3 | 4 | 918.1 | 876.1 | 590.1 | 18.08 | 26.9 |
| 2 | 4 | 775.0 | 725.8 | 517.4 | 17.97 | 18.8 |

All sampled pages and scroll destinations finished rendering. Visible bitmap
resolution was not reduced. Reducing prefetch to two pages saves little peak
memory here, while some page jumps lose their already-rendered image:
QUYEN 2.2 page 6 was cached with four instances/four-ahead, but took 421 ms with
two-ahead and 492 ms with two instances/four-ahead.

## Runs Including 100% Zoom

| PDFium instances | Peak private MiB | After idle MiB | QUYEN 2.1 zoom ms | QUYEN 2.2 zoom ms | Return ms |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 4 | 1092.0 | 628.1 | 149.0 | 413.0 | 20.6 |
| 3 | 1010.8 | 614.8 | 172.5 | 716.7 | 17.5 |

Here three instances save 81.2 MiB of peak memory, but the second file's zoom
takes about 304 ms longer. This is a tradeoff, not a universal speedup.

## Interpretation

- The image cache occupied roughly 60-63 MiB and managed memory roughly
  51-56 MiB. Most private memory is outside those two measured components,
  including native PDFium state, WPF resources and allocator retention.
- Two documents can have eight PDFium document replicas with four instances,
  six with three, or four with two. Extra instances improve concurrency but
  can duplicate parsed PDF state. The 335 MiB source buffer is disk-backed;
  it must not be counted as a 335 MiB managed byte array.
- Existing idle reclamation materially reduces memory. One short session does
  not establish a leak or prove that long-term growth is bounded.
- Dispatcher-delay P95 was about 21-30 ms beyond a nominal 16 ms timer. These
  timer statistics and simulated scroll ticks are not display FPS or a direct
  measurement of the user's visible desktop responsiveness.
- Runs were sequential while the user continued other work. Native scheduling,
  network reads, OS caches, allocator behavior, and other workloads introduce
  variation. Each configuration has only one run per scenario.

## Preliminary Recommendation (Superseded)

Keep the four-page prefetch and current four-instance default for now. On these
files, blindly shrinking the image cache or reducing prefetch does not address
the dominant memory cost. Three instances are an optional memory/latency
compromise; two save more RAM but have less background rendering capacity.

The next optimization to investigate is retiring duplicate native document
replicas during genuine idle periods while preserving concurrency during active
scrolling. That requires a post-idle zoom/tab-switch comparison before changing
production policy. This profiling work does not change production defaults.

JSON evidence is under `Tests/bin/Release/net10.0-windows10.0.19041.0/results/`:
`heavy-background-four-workers-four-ahead.json`,
`heavy-background-four-workers-two-ahead.json`,
`heavy-background-three-workers-four-ahead.json`,
`heavy-background-two-workers-four-ahead.json`,
`heavy-background-balanced-zoom-repeat.json`, and
`heavy-background-default-zoom-repeat.json`.
