# Adaptive RAM policy: next session (2026-10-06)

## Status

Wide-scroll follow-up is implemented and tested on the user's complete 284-page
drawing PDF. See [bounded memory implementation and measured tradeoffs](WIDE-SCROLL-RAM-IMPLEMENTATION-2026-10-06.md).
It adds a 512MiB normal cache ceiling, independent app-private thresholds,
weighted render reservations, viewport eviction and rate-limited background GC.
The checklist below records earlier requirements; the measured follow-up supersedes
its idle-only GC suggestion and large-machine cache-growth policy.

Adaptive retention is implemented in the working tree on 2026-10-06. See
[implementation and validation](ADAPTIVE-RAM-IMPLEMENTATION-2026-10-06.md).
The balanced MuPDF startup now starts its own memory controller, including
child-worker private commit and adjustable synchronized bitmap-cache budgets.
The design checklist below records the original requirements. Accepted
rendering and zoom behavior from MUPDF-BALANCED-MIGRATION.md is preserved.

## Goal

Use more cache when Windows has spare memory, and yield memory when other
applications need it. Grow on demand, not by allocating unused RAM in advance.
Never evict the currently displayed sharp image merely to replace it with blur.
Protect visible pages and their immediately useful zoom data. Under real memory
pressure, revisiting an evicted page may be slower; current interaction wins.

## Controller

- Sample about every two seconds off the UI thread: available physical memory,
  physical memory load, system commit headroom, parent private commit and all
  MuPDF worker private commit. Private commit is not resident physical RAM.
- Use normal, pressure and critical states with separate recovery thresholds
  and a sustained recovery period (initial trial: 30-60 seconds).
- Derive thresholds from machine RAM and retain an OS/other-app reserve. Do not
  ship one fixed MiB threshold as optimal for every machine.
- Coordinate a global retention target across caches. Shared bitmap references
  mean adding each cache's reported bytes overcounts some retained images;
  cache eviction alone may not free a bitmap still referenced by a view.
- Restore budgets gradually after recovery. Do not immediately refill caches
  or restart every background worker.

## Ordered Response

1. Trim inactive-tab and distant-page images, then obsolete zoom levels.
2. Reduce speculative render/prefetch work and cancel obsolete queued requests.
3. Release least-used native documents/display lists in idle workers.
4. Under stronger pressure, retire idle background workers and reduce future
   background concurrency. Preserve foreground capacity when possible.
5. Keep visible image quality and zoom presentation unchanged. Define a minimum
   working-memory floor; unlimited speed cannot be promised below that floor.

Do not force GC or empty the working set during zoom/pan. Dropping references
does not guarantee an immediate drop in Task Manager. Consider idle-only GC
only if measurements show a benefit, not as the primary retention strategy.

## Implementation Map

- Services/BitmapMemoryCache.cs: adjustable budget and trim API; preserve caller
  locking and pinned-image semantics.
- Services/ReaderPageRenderCache.cs: change budgets under its own synchronization.
- Services/ExperimentalMuPdfViewport.cs: bridge cache adjustment, child memory
  accounting, idle worker native-cache trimming through the existing IPC gates.
- Controls/ContinuousPdfView.cs: region cache trim under RegionCacheLock and
  explicit visible/live-image protection. Account for PageState references.
- Services/ThumbnailCache.cs and other caches: audit ownership and retention
  before including them in the global controller.
- Add diagnostics for pressure state, assigned budgets, actual retained bytes,
  worker counts and trim reasons. No timer-driven concurrent cache mutation.

## Verification Before Selecting Defaults

- Inject fake memory samples to test transitions, hysteresis, recovery,
  protected images and disposal without consuming the user's real free RAM.
- Repeat the drainage page-one and all four BinhDo-page workloads, including
  cached revisits, nearby zoom, deep zoom, pan and multiple tabs.
- Record parent/worker private commit separately from resident working set;
  compare render latency and cache-hit rates before/after pressure and recovery.
- Confirm no sharp-to-blurry regressions, protocol corruption, worker restart
  loops, UI-thread stalls, or source-file changes.
- Run migration/lifecycle and heavy-file/offscreen zoom checks again.
- Keep tests in the background; do not automate or close the user's windows.

Implement adaptive cache retention first. Disk cache and GPU work are separate
follow-ups, not prerequisites for this change. Original thresholds and the
working-memory floor still require calibration on the supplied CAD workloads
and a wider range of machines; the implementation report records this run.
