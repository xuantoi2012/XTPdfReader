# Foxit desktop comparison - 2026-10-02

## Scope and comparability

This is an exploratory GUI/resource profile, not a statistically controlled engine
benchmark or a display-FPS test. No production renderer settings or code were changed
in this measurement pass.

- Installed comparator: Foxit PhantomPDF **10.0.0.35798**, not the newest Foxit Reader.
- Reader: existing Release build, product revision `a779ab4`, with the current dirty
  workspace's previously built tuning changes; two PDFium instances.
- One source: `03. QUYEN 2.2 - TNM, TNT, HKT, CAY XANH, CHIEU SANG, TCTC.pdf`,
  169,948,002 bytes (162.08 MiB), 276 pages. No PDF editing or saving was performed.
  Final source timestamp remained 2026-09-29 14:48:41.9327581 +07:00.
- Monitor 1, mostly maximized 1920 x 1040 windows. Reader's reading viewport is
  approximately 1860 x 838; Foxit's is approximately 1865 x 812, so not identical.
- **Match actual page pixels, not zoom labels:** Reader 100% gives about 1100 pixels
  across the paper; Foxit 69.3% gives about 1102. Reader 300% is approximately Foxit
  208%. Comparing both at their respective 100% would be misleading.
- Foxit restored page 11; Reader initially showed the cover, then page 11. Both
  visited page 81, returned to 11, zoomed deeply, panned, reduced zoom, and opened /
  scrolled / revisited thumbnails. Foxit shows about five thumbnails in one column;
  Reader shows about twelve in two columns. Deep-zoom anchors also differ, so the
  same page scale does **not** mean the same crop or equal raster work.
- OS/network caches were not flushed. Other user applications remained running.
  The order, elapsed idle time, page-prefetch policy, and foreground/background state
  were not controlled closely enough to calculate a speedup ratio.

## Capture method

`Record-DesktopResources.ps1` sampled process CPU time, private bytes, working set,
threads, handles, and generic process I/O roughly every 0.12-0.13 seconds. CPU-core
equivalent is delta CPU seconds / elapsed seconds, not percent of this 28-thread CPU.
Process I/O includes more than disk reads; it cannot identify a disk cache location.

The initial calibration run queried Foxit's PDF accessibility text and accidentally
selected all document text while attempting to select a numeric field. Its abnormal
memory growth is excluded. The effects of accessibility queries and select-all were
not separated. Foxit was restarted for the actual resource pass, and PDF snapshots
then used `include_text: false`. One pan starting over text selected visible text;
that action is not counted as a successful pan. Pan from blank paper worked.

Portable PresentMon 2.6.0 was run without elevation against the Foxit PID. The
300-second interaction capture finished but produced no usable per-process frame CSV.
This does not prove Foxit lacks GPU acceleration. No FPS, input-to-photon latency,
or presentation smoothness ranking is claimed. Present events would not by themselves
prove that the correct PDF content was ready either. See the
[official console documentation](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md).

Raw captures remain under ignored `Tests/bin/desktop-comparison/`:
`resources-20261002.csv` (excluded calibration) and
`resources-clean-20261002.csv` (accepted process pass, 06:07-06:27 UTC).
Both collectors and both owned PresentMon sessions completed and stopped.

## Process memory

Values below are ten-second means in MiB, taken from the named UTC windows in
`desktop-comparison-20261002-windows.json`. They describe these GUI workflows,
not equal-work renderer benchmarks. Private bytes are committed private memory,
not physical RAM currently resident; working set also includes shared/mapped memory.

| Phase | Foxit private | Reader private | Foxit working set | Reader working set |
|---|---:|---:|---:|---:|
| Start, no PDF open | 43.46 | 173.01 | 105.25 | 177.97 |
| Page 11, normal zoom, initial visit | 105.11 | 568.64 | 174.44 | 524.41 |
| Page 81, normal zoom | 197.55 | 734.67 | 266.54 | 708.32 |
| Page 11, deep zoom | 116.42 | 824.49 | 185.32 | 730.31 |
| Normal zoom after pan and thumbnails | 191.60 | 874.85 | 261.07 | 806.31 |

Accepted-pass private peaks: Foxit **199.62 MiB**, Reader **959.03 MiB**. The
Reader peak occurred during reduction to normal zoom, not necessarily while the
deep-zoom crop was largest. These are sampled peaks, not exhaustive allocation peaks.

Supplemental cover check, after the CSV capture: Foxit was restored to a smaller
1693 x 830 window, still at 69.3%. Private memory was 195.82 MiB before navigating
to page 1 and 189.36 MiB at 06:30:41.752 UTC with the cover visibly drawn. This
does not fix the ordering/viewport differences, but the extra cover visit did not
produce Reader-sized memory consumption in Foxit.

CPU samples are reproducible with the summarizer, but are not used to rank engine
speed: the windows contain different background/preload work and focus states. For
example, page-11 normal windows measured 0.1140 / 0.1218 core equivalents for
Foxit / Reader; the post-workflow windows measured 0.0488 / 0.1945. A separate
background Foxit window measured 0.0251. None is a matched interaction CPU benchmark.

## Cache and presentation observations

- Both apps had content in the first observed screenshot when returning from page
  81 to the previously viewed page 11. Reader's current warm reuse is working here.
- Reader's first snapshots of newly visited pages 11 and 81 showed white paper;
  later snapshots contained the page. This is a cold-page feedback gap to improve.
- Reader retained content when zooming and panning. The first 200% observation showed
  enlarged, softer content before refinement; 300% observations had sharp content.
- Both apps' visible thumbnails were populated when opening/revisiting the panel.
  This limited scroll did not establish a thumbnail-cache failure in Reader.
- Automation input-to-snapshot intervals include helper IPC, input injection, and
  capture delay. Snapshots can lag. Reader's later ready snapshots were separated by
  long tool/model gaps. **Do not report those gaps as render latency**, or infer that
  one app is faster simply because its snapshot call returned sooner.
- No evidence identifies whether Foxit's reuse is bitmap, display-list, parsed-object,
  RAM, or disk based. Copying an assumed Foxit disk-cache architecture is not justified.

## Reader diagnostics and reclamation

At 06:19:18 UTC, the Reader Debug report showed:

| Item | Measured value |
|---|---:|
| Private / working set | 889 / 810 MiB |
| Managed heap | 32 MiB |
| Page image cache / region cache / thumbnails | 53 / 11 / 12 MiB |
| Native heap committed / allocated | 614 / 492 MiB |
| Native document / page handles | 2 / 10 |
| Page loads / native cache hits | 70 / 11 |
| Native gate wait, mean / max | 20.7 / 731.0 ms |
| Native raster slice, mean / max | 8.4 / 205.0 ms |
| WPF bitmap copy, mean / max | 0.9 / 5.0 ms |
| WPF rendering tier | 2 |

The 162 MiB source-buffer figure is the logical size of a temporary-file buffer,
**not** another 162 MiB managed byte array to add to private memory. Native-heap
counters and the residual estimate do not uniquely attribute allocations to PDFium.
Bitmap-copy timing does not include WPF composition/presentation or GPU uploads.

Important correction to the live observation: the large RAM drop happened **before**
the manual button. CSV shows about 889 MiB through 06:19:36, then 512 MiB by
06:19:46, consistent with the existing idle-native-reclamation policy. The Debug
window was open during this segment, so it was not a pure idle CPU test.

The combined native-trim/GC button was pressed at 06:20:00. Its report measured
492 -> 487 MiB **after the native trim had already run**, so that label records only
the GC portion, not the full operation. Documents/pages then read 0/0, image caches
remained 53/11/12 MiB, and native heap committed/allocated read 421/177 MiB.
The 06:20:25-35 window averaged 486.82 MiB private.

Revisiting page 81 after this produced white paper in the first snapshot and required
content again: preserving bitmap-cache totals does not guarantee every old page is
still cached. After another long idle, private/working-set means were 455.66/377.90
MiB at 06:23:20-30. Do not force full trim/GC on every zoom to chase a smaller number.

## Priorities before considering C++

1. Profile native allocation ownership and reclaim parsed pages/documents selectively.
   The current policy works, but high active-session retention dominates this workflow.
   Avoid duplicating heavy document state across more PDFium instances without benefit.
2. Preserve useful page/crop/thumbnail images across native reclamation, with a
   memory-bounded recent-page preview tier. Give new pages useful feedback sooner
   without restoring expensive all-tab/all-page warmup.
3. Measure native parse/raster, queue/gate delay, bitmap copy, and WPF presentation
   separately under repeated, fully matched inputs. Investigate the zoom-down
   allocation peak and perceived scaling transition. Current copy timings do not
   identify WPF as the main bottleneck.
4. Profile the 173 MiB empty Reader baseline for avoidable eager initialization.

Keep the current two-instance default while testing those changes. This pass supports
improving native retention and display caching within the existing app; it does not
establish that WPF requires a full C++ rewrite or that an optimal setting is final.

## Reproduce resource summaries

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tests/Summarize-DesktopResources.ps1 -CsvPath Tests/bin/desktop-comparison/resources-clean-20261002.csv -WindowsPath Tests/desktop-comparison-20261002-windows.json
```

The capture helper is read-only process telemetry; UI actions require separate,
authorized Computer Use. Both helper scripts were verified with Windows PowerShell
5.1; the summarizer was also run with PowerShell 7. No production build was required
for this report-only pass; the preceding Release regression results are not new
verification of these measurements.

The subsequent [background retention and preview follow-up](FOXIT-RETENTION-2026-10-02.md)
implements and verifies bounded recent-page previews and focused idle document
consolidation. Its offscreen numbers are separate from this GUI comparison.
