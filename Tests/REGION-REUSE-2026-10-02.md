# Bounded viewport-region reuse, 2026-10-02

## Decision

Enable spatial reuse for deep-zoom viewport crops, without adding a new PDF
engine, editor features, more PDFium replicas, or larger cache budgets.

Keep an existing compatible image while its replacement renders. Reuse pixels
at the same raster dimensions, content version and layer token. Render one
missing band, then compose a frozen bitmap on a worker. If gaps are fragmented,
or their bounding band exceeds half the target area, render one complete crop.
Fully covered targets require no native render. There is never an additional
native pass per gap in the selected policy.

PDFium clip edges need special handling: exclude 16 pixels at cached boundaries
that become interior seams, render the new band with a 32-pixel gutter, and copy
only its requested coverage. Copying the entire gutter overwrote good cached
pixels with clipped glyph edges in the first prototype. Unchanged outer crop
borders and actual page boundaries remain usable.

The implementation retains two live regions per page and the existing 16 MiB
region-cache budget. Composition uses a temporary unmanaged buffer, freed on
success, failure and cancellation, rather than a new large managed byte array.
Late results cannot publish into a closed document, old content version or old
layer view. Other reader/native-cache settings are unchanged.

## Method

Release x64, PDFium package 147.0.7690, default two-replica policy. Each run was a
fresh below-normal-priority test process, with a nonactivating WPF window placed
offscreen. No Computer Use, mouse/keyboard automation, Foxit attachment, or user
window manipulation was used.

Page 11 of each original heavy PDF, 300% zoom, 1263 x 792 actual viewport,
1200-unit page base width, DPI scale 1. Source buffering and the initial readable
page/crop were warmed before active timing. Whole-page reader cache: 64 MiB;
prefetch disabled for this isolated region comparison. Fifteen pans: six
(80, 180), six (-80, -180), three (-160, 0), with 60 ms between completed steps.
Private bytes sampled every 16 ms through initial load and the active sequence,
then sampled again after 25 seconds idle. Original files were SHA-256 checked
before/after each run and remained unchanged.

Three runs per mode per file, sequential, with order reversed on the second
repeat. Tables are medians of the three run totals, not pooled step medians.
These tests use one visible placement backed by the full source PDF. They are
not a measurement of the complete multi-tab shell, input-to-display latency,
GPU frame pacing, cold startup, or parity with Foxit.

Raw results: `Tests/bin/Release/net10.0-windows10.0.19041.0/results/`,
`region-pan-band-{false,true}-{2,3}-r{1,2,3}.json`.

## Results

| File / metric | Reuse off | Reuse on |
| --- | ---: | ---: |
| QUYEN 2.2, about 162 MiB: summed sharp-readiness waits | 305.45 ms | 274.27 ms |
| Render-batch elapsed total | 227.74 ms | 206.46 ms |
| Active process CPU | 1.469 s | 1.391 s |
| Peak private bytes | 253.74 MiB | 249.28 MiB |
| Private bytes before idle | 253.74 MiB | 211.54 MiB |
| Private bytes after 25 s idle | 203.89 MiB | 168.63 MiB |
| QUYEN 2.1, about 173 MiB: summed sharp-readiness waits | 205.06 ms | 203.72 ms |
| Render-batch elapsed total | 132.33 ms | 116.51 ms |
| Active process CPU | 1.828 s | 1.844 s |
| Peak private bytes | 255.08 MiB | 250.98 MiB |
| Private bytes before idle | 220.64 MiB | 223.54 MiB |
| Private bytes after 25 s idle | 176.97 MiB | 178.09 MiB |

Both files: 13,864,960 -> 11,523,072 raster pixels (-16.89%), eight native
passes in either mode, final shared region cache 11.72 MiB in either mode.
`NativeMs` in JSON is elapsed time around the async render-batch call, including
service scheduling and continuation delays, not pure raster CPU time.

The first file improved aggregate readiness by 10.2% and idle private bytes by
35.3 MiB. The second file's readiness and idle memory are effectively neutral;
the lower batch time does not translate into a comparable UI gain. Private bytes
are not working set, and transient/native allocation behavior varies between
documents. Do not extrapolate the first file's RAM improvement to every PDF.

A rejected two-pass prototype reduced raster pixels further, but extra page
object traversal and composition negated some latency savings. The selected
one-band policy preserves the one-pass behavior on diagonal/low-overlap pans.

## Correctness and limits

- `--scroll-quality`: 146 checks passed.
- `--background-regression`: 391 checks passed; interactive dialog tests are
  deliberately omitted by this background mode.
- Byte-exact synthetic composition, 80 seeded geometry cases, poisoned gutter
  exclusion, diagonal fallback, native thin paths/transparency/large text,
  cache budgets, invalidation, layer-token changes, rotation and late cancellation.
- Separate `--profile-quality --profile-quick` runs compare visible pixels to
  direct native crops after all 15 pans. QUYEN 2.2 and QUYEN 2.1: maximum visible
  channel difference 1/255; Drawing-16: 0. The native mixed-content fixture allows
  at most 2/255 rounding on under 5% of pixels and passed with 25,264/694,400
  changed pixels. Native crops with different clip origins are not bit-identical.
- QUYEN 2.2's final complete crop differs by up to 135/255 in its outer margin
  (x=0..13), outside the visible rectangle (x=176..1438). This is recorded rather
  than claiming that all offscreen pixels match the direct-crop reference.
  Every visible frame in the stepwise quality run passed.
- Quality-mode CPU/memory numbers include reference renders and pixel arrays;
  they must not be included in performance medians. Quick mode omits idle trim.
- This change targets pan/scroll crop refinement at deep zoom. Zoom frame pacing,
  additional page types, larger viewports/DPI and the full many-tab workflow still
  need broader interactive validation. It does not establish Foxit-level smoothness.

## Reproduce

Build `Tests/PerformanceTests.csproj` in Release, then run the generated test EXE:

```powershell
.\XTPdfMergeApp.PerformanceTests.exe --scroll-quality
.\XTPdfMergeApp.PerformanceTests.exe --background-regression
.\XTPdfMergeApp.PerformanceTests.exe --region-pan-profile off false 3 --profile-sources '<manifest.json>'
.\XTPdfMergeApp.PerformanceTests.exe --region-pan-profile on true 3 --profile-sources '<manifest.json>'
.\XTPdfMergeApp.PerformanceTests.exe --region-pan-profile quality true 3 --profile-sources '<manifest.json>' --profile-quality --profile-quick
```

The manifest is the existing `RecentFile[]` profile-source JSON; source selection
uses a zero-based index. Keep A/B runs sequential and reverse run order. The
off switch is an internal test hook, not a new persisted user setting.
