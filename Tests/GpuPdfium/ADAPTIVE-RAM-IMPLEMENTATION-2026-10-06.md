# Adaptive MuPDF memory retention — 2026-10-06

Implemented on top of `64716a4`, retaining the existing 4608px whole-page tier,
crop geometry and zoom/pan presentation. Changes are in the working tree.

## Runtime behavior

- A serialized timer samples every two seconds off the WPF dispatcher. Windows
  physical availability/load comes from `GlobalMemoryStatusEx`; global commit
  headroom comes from `GetPerformanceInfo`. Parent and child-worker private
  commit are measured separately and are not interpreted as resident RAM.
- Pressure begins at 85% physical load, a low OS reserve (max of 512 MiB and
  10% of physical RAM), or low commit headroom (max of 256 MiB and 5% of commit
  limit). High combined app/worker private commit also triggers pressure when
  spare physical RAM is below 1.5 times the reserve. Critical thresholds are
  94% load or half the reserve/headroom. These are initial tunable code defaults.
- Recovery requires 45 seconds below 78% physical load, physical availability
  above 1.5 times the reserve and commit headroom above twice its reserve.
  Budgets grow by at most 64 MiB per sample; growth allocates no unused memory.
- Normal aggregate cache-retention target uses the lower of 20% of machine RAM
  and half the physical memory above the reserve, clamped to 256–4096 MiB.
  Pressure target is RAM/32 clamped to 256–768 MiB; critical target is 256 MiB.
  Allocation: reader 40%, bridge 40%, regions 15%, thumbnails 5%; reader previews
  are included inside its allocation. The floor is a retention floor, not a
  guarantee that every workload fits in that amount of total process memory.
- Weakly registered views publish immutable page/bitmap protection snapshots
  on their dispatcher. Current, held and pending presentation viewports plus
  immediately adjacent pages are protected. Budget trimming pins the actual
  live bitmap references, allowing unused zoom versions of the same page to
  be evicted. Views retain their displayed page/crop images even over budget.
- Under pressure: release distant/inactive view state, cancel obsolete reader
  requests, wide-region and zoom speculation, stop whole-document warming and
  pause background sidebar warmup. Page prefetch falls from four to one, then
  zero in critical state; preview-ahead falls from eight to two, then zero.
- New background renders use one lane under pressure. In critical state,
  speculative work stops; necessary sidebar/metadata commands may still start
  a background process on demand. The two foreground lanes remain reserved.
- Native requests carry the pressure state, so document/list limits reduce to
  one document and two lists (pressure) or one list (critical), and restore on
  the next normal request. Idle trim preserves visible native pages/documents,
  releases distant documents/lists and shrinks MuPDF's store. It acquires each
  worker's existing IPC gate without waiting, skips workers with queued requests or active in the last
  two seconds, drains replies and rate-limits trims to once per ten seconds.
- After ten idle seconds, pressure retires the second background worker;
  critical pressure retires both background workers. Trim never starts a worker.
  Restoring budgets does not preemptively restart workers or refill caches.
- Shutdown disposes the timer and cancels pending dispatcher work/late samples.
  No forced GC or working-set purge is added to interaction or memory policy.

The bridge and reader can share bitmap references. Assigned budgets are
conservative cache-ownership limits, not an additive physical-RAM estimate or
a hard cap on parent plus worker memory. WPF, native allocations and transient
render buffers can outlive cache eviction until normal collection/release.

## Offscreen real-PDF profile

This workspace used the local 173,799,414-byte `03. QUYEN 2.2 - TNM, TNT, HKT,
CAY XANH, CHIEU SANG, TCTC.pdf` and 20,599,234-byte `XT Sheet Set2.pdf`:
first two pages of the former, first four of the latter, plus background previews.
The original supplied drainage/BinhDo pair was not used in this run. Pressure
was injected as controller decisions; no real OS memory shortage was induced.

One sequential run, naturally warm OS caches, no forced GC:

| Phase | Parent private MiB | Parent working set MiB | Worker private MiB | Workers | Bridge retained MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Normal | 552.6 | 597.0 | 261.4 | 4 | 258.5 |
| Pressure | 571.1 | 619.0 | 251.3 | 4 | 172.6 |
| Critical, after idle retirement | 566.4 | 613.2 | 119.5 | 2 | 86.6 |
| Recovery, deep zoom and pan | 469.4 | 519.1 | 119.7 | 2 | 118.0 |

Cache ownership and worker private commit decreased. Parent private commit did
not immediately decrease during pressure; this run does not establish a total
RAM reduction percentage or frame-time/latency guarantee. The protected page
revisit was a cache hit (one observed 0.234ms API call). Offscreen frame pixel
hashes were identical before/after pressure and critical trimming. Nearby zoom
kept the same sharp bitmap; deep zoom produced real sharp regions and pan kept
the sharp composite/fallback. Both source PDF SHA-256 hashes remained unchanged.
No user window was activated, automated or closed.

Raw profile: ignored test output
`Tests/bin/Release/net10.0-windows10.0.19041.0/results/adaptive-memory-profile.json`.

## Validation and reproduction

Release build succeeds, with the existing CS8602 warning in `UiSmokeTests.cs:117`.
Focused checks cover pressure/critical transitions, commit pressure, machine
scaling, invalid probes, hysteresis/recovery, pinned bitmap eviction, stale zoom
versions, cancellation of queued work, native trim/retirement, continued visible
rendering, lazy native recovery, timer application/disposal and source integrity.
Passed: 38 focused adaptive checks, 20 real-PDF profile checks, 86 MuPDF
migration checks, 36 real render/cache checks, four nearby-zoom checks and two
preview lifecycle checks. Profile timings are observations, not performance limits.

```powershell
dotnet build Tests/PerformanceTests.csproj -c Release
$checks = 'Tests/bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.PerformanceTests.dll'
dotnet $checks --adaptive-memory-check
dotnet $checks --mupdf-migration-check
dotnet $checks --preview-warm-lifecycle
dotnet $checks --mupdf-fast-check '<first PDF with two pages>' '<second PDF with four pages>'
dotnet $checks --mupdf-throughput-viewer-check '<first PDF>'
dotnet $checks --adaptive-memory-profile '<first PDF with two pages>' '<second PDF with four pages>'
```

Offscreen integration/profile checks use nonactivating windows outside the
desktop. The production build lives in `bin/Release/net10.0-windows10.0.19041.0`.
Remaining calibration: original CAD pair, more simultaneous documents/views,
long-running actual OS pressure/recovery and different RAM/commit configurations.
The fake-sample tests validate transitions without stressing the user's free RAM.
