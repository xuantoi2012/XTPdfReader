# Reader performance modes — 2026-10-06

Historical first implementation. The current five tiers and Balance default are
documented in [USER-FEEDBACK-2026-10-06.md](USER-FEEDBACK-2026-10-06.md).

Implemented in Settings → Performance & memory → Reader performance.
Existing installations without a saved value use **Memory saving**, preserving
the bounded-memory policy validated on the user's drawing PDF.

| Setting | Memory saving (default) | High performance | Maximum performance |
| --- | ---: | ---: | ---: |
| Normal aggregate bitmap cache ceiling | 512MiB | 1024MiB | 2048MiB |
| App + workers private soft / urgent threshold | 1024 / 1536MiB | 2048 / 3072MiB | 4096 / 6144MiB |
| Estimated concurrent raster reservations | 192MiB | 256MiB | 384MiB |
| Sharp pages prefetched ahead | 1 | 2 | 4 |
| Normal display-list count per render worker | 4 | 8 | 12 |
| Cold-worker recycle threshold | 320MiB | 512MiB | 768MiB |
| Background worker normal idle retirement | 30s | 90s | 180s |
| Minimum normal idle background-GC interval | 8s | 20s | 45s |
| Distant sharp cache entries | Remove promptly | Retain within LRU budget | Retain within LRU budget |

These are policy parameters, not physical-RAM or process-memory hard caps.
Cache ceilings also scale down with machine headroom; growth is gradual and
does not allocate or refill memory in advance. Large raster requests can run
alone instead of deadlocking on their reservation limit.

Changes apply to the active reader on the next controller sample (normally
within one second) and subsequent rendering; no restart is needed to change a
mode. A mode change causes the viewport to reschedule prefetch. Existing renders
finish safely; shrinking raster limits blocks new work until capacity returns.
Lowering the mode immediately lowers the cache ceiling and removes cold images.
Raising the mode re-evaluates pressure without inheriting the old app threshold.

All modes still observe Windows physical/commit pressure. Under pressure, they
discard distant resources, reduce raster reservations to 192MiB, prefetch at
most one page (zero at critical pressure), limit native resources, retire idle
background workers after 10s and permit rate-limited background GC every 3s.
The displayed sharp images remain protected. Two foreground lanes are retained.

The selection is stored as `ReaderPerformanceMode` in the existing HKCU app
settings key. Missing/invalid values fall back to MemorySaving. Test profiles
override only their own process; the user's registry selection was not changed.

## Validation

- Isolated Release build succeeded; existing CS8602 in UiSmokeTests.cs remains.
- 51 adaptive policy checks include healthy Maximum, low-memory Maximum,
  switching up/down, invalid-profile fallback and gradual cache growth.
- 71 reader memory regression checks and 86 MuPDF migration checks passed.
- Four Settings checks passed; offscreen render inspected visually. The selector,
  saved selection and descriptive hint are visible without layout clipping.
- Three fresh, sequential 100-page runs on the user's 284-page Desktop PDF,
  with the production shared reader cache, reverse jumps, nearby/deep zoom,
  pan and 12s idle: 25 checks passed per run. PDF source hashes unchanged.

| Mode | Peak private MiB | After idle MiB | Sharp-arrival median ms | Deep zoom ms |
| --- | ---: | ---: | ---: | ---: |
| Memory saving | 1334.2 | 743.1 | 260.0 | 78.8 |
| High | 2101.2 | 785.3 | 264.7 | 94.1 |
| Maximum | 2823.6 | 2358.9 | 187.7 | 79.1 |

Private commit sums the benchmark parent and its workers; it is not resident
RAM. High reached its 2GiB soft threshold and reclaimed during this run, so a
higher mode did not make this workload uniformly faster. Maximum retained more
history and had a lower median sharp-arrival time in this single screening.
These are one run per mode, not a statistically repeated performance claim.
Every moving frame's sharpness and FPS are not measured by this harness.

## Build and repeat

The user had the existing Release EXE open, locking its apphost. The new build
was placed in `bin/PerformanceModes/` without closing the user's app or documents.
It contains `XTPdfMergeApp.exe` and the prepared MuPDF runtime. Close the old app
and launch this EXE to use the new Settings selector.

```powershell
dotnet build Tests/PerformanceTests.csproj -c Release -p:OutputPath=C:/Users/condu/source/repos/xuantoi2012/XTPdfReader/bin/PerformanceModes/
dotnet bin/PerformanceModes/XTPdfMergeApp.PerformanceTests.dll --wide-scroll-memory-profile 'C:\Users\condu\Desktop\03. QUYEN 2.2 - TNM, TNT, HKT, CAY XANH, CHIEU SANG, TCTC.pdf' maximum 100 --reader-cache --performance-mode Maximum
dotnet bin/PerformanceModes/XTPdfMergeApp.PerformanceTests.dll --performance-settings-check
```

Raw JSONs and the Settings image are in `bin/PerformanceModes/results/` (ignored).
