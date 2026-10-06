# Performance tiers and reader feedback — 2026-10-06

This revision supersedes the defaults and three-tier table in
READER-PERFORMANCE-MODES-2026-10-06.md. Existing explicit preferences remain;
the old `High` preference maps to `Balance`. Missing/invalid preferences use
Balance. The user's current saved setting is Maximum and was not overwritten.

| Mode | Normal aggregate image cache | Private soft / urgent | Raster reservations | Prefetch | Worker native lists | Background idle retirement |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Memory saving | 512 MiB | 1024 / 1536 MiB | 192 MiB | 1 | 4 | 30 s |
| Balance (default; formerly High) | 1024 MiB | 2048 / 3072 MiB | 256 MiB | 2 | 8 | 90 s |
| Performance | 1280 MiB | 2560 / 3840 MiB | 288 MiB | 3 | 9 | 120 s |
| Ultra performance | 1536 MiB | 3072 / 4608 MiB | 320 MiB | 3 | 10 | 150 s |
| Maximum performance | 2048 MiB | 4096 / 6144 MiB | 384 MiB | 4 | 12 | 180 s |

All modes retain adaptive Windows physical/commit pressure checks. Cache and
raster budgets are not process-memory caps; a single oversized raster may run
alone. Pinned images can exceed a cache budget.

## Inactive tabs

The last page of each open tab retains its sharp full-page bitmap and viewport
regions. Closing the tab releases its protection; editing the source or changing
layers invalidates obsolete images. Native resources are protected only for the
active viewport, so inactive tabs do not keep workers/display lists alive merely
because their sharp image is retained.

Returning to a tab restores its selected page, zoom and precise scroll offset
when viewport geometry still matches. Rebinding previously allowed an initial
page-change notification to overwrite the requested target page; the target is
now captured before rebinding. An unchanged, already rendered page/crop is reused.
New zoom, viewport geometry, content, or previously uncovered areas can still
require a render.

## Page operations and presence

Single-source page rotation uses the existing incremental writer directly after
suspending app-owned document handles. It no longer replaces the file, allowing
external readers that share write access but deny replacement to remain open.
The writer checks source length, retries a conflicting save against the new
source, and rolls back an incomplete append. A true exclusive write lock still
prevents modifying the source. Toolbar view rotation remains a separate operation
that does not write the PDF. Multi-source edits retain staged atomic replacement.

Deleting the current page selects the nearest following surviving page, or the
preceding page at the end. Deleting other pages retains the current page object.
The eraser ribbon tool now uses the Phosphor eraser geometry.

Presence is enabled by default and checked when the active document changes;
the existing thirty-second refresh remains. A persistent status label shows
other readers, with full names in its tooltip. These names come from other app
instances publishing presence markers; this does not identify arbitrary Foxit,
Acrobat, or Chrome users. Explicitly disabling presence remains respected.

## Fonts on the reported PDF

Source: `P:\01-PIP\072 Duong AIII noi dai\BCDXCTDT\C-Design\01-Publication\2026.09 BCDXCTDT DUONG QH AIII GĐ2.pdf`

115 pages, 18,197,417 bytes. SHA-256:
`d18a12700005f64c3c04a31544fb3e56906993d9345c6a672c1ffdc53ff7f19d`.

The PDF mixes embedded Vietnamese glyph fonts with unembedded Windows TrueType
fonts. Generic fallback produced inconsistent shapes/weights. The worker maps
unembedded fonts to installed Windows fonts and attaches their streams only to
its private in-memory document. It inspects fonts of each requested page, rather
than scanning every object of a large PDF at startup. Streams are reused within
that document. Embedded fonts and the source bytes are preserved. Unavailable
fonts keep the engine's existing fallback.

Verification used Segoe UI variants, Bahnschrift, Times New Roman, Arial and
Calibri. The first three pages retain dimensions and identical extracted Unicode
text; corrected renders were inspected visually. The original source hash is
unchanged. Real-file rotation tests use a local copy, not the network original.

## Validation

Release build passed (one existing nullable warning in UiSmokeTests.cs:117).
The normal solution Release build also passed with zero warnings/errors; the
main executable is `bin/Release/net10.0-windows10.0.19041.0/XTPdfMergeApp.exe`.

- User feedback integration: 18 checks, including inactive sharp/full-page and
  deep-zoom crop reuse after critical trimming, release on close, invalidation,
  page deletion, precise tab position, eraser icon, immediate presence, and
  incremental rotation with another read handle denying replacement.
- Real feedback PDF: 5 checks; rendering before/after rotation, original-byte
  prefix preserved in the edited copy, all 115 pages retained, source unchanged.
- Adaptive memory: 57 checks, including migration/defaults and intermediate tiers.
- Existing reader memory regression: 71 checks.
- MuPDF migration: 86 checks.
- Settings: 4 checks and rendered settings screenshot.
- Real PDF wide-scroll profile: 27 checks, 115 pages forward plus reverse revisits,
  deep zoom and twelve seconds idle, using the real worker and reader cache.
- Windows font fallback script passed text/dimension/source-hash assertions.

Final Balance screening run:
`bin/UserFeedback/results/wide-scroll-feedback-final-balance-20261006-141736.json`.
Measured parent-plus-worker private peak 2165.17 MiB, final 935.59 MiB; sharp-image
arrival median 860.67 ms, deep zoom 731.06 ms. This is one offscreen screening run,
not a repeated UI benchmark or comparison against Foxit/Chrome. Retaining inactive
tab images intentionally adds memory proportional to the number of open tabs.

Validated runnable build:
`bin/UserFeedbackValidation/XTPdfMergeApp.exe`.

Reproduction:

```powershell
dotnet build Tests/PerformanceTests.csproj -c Release --nologo -p:OutputPath=C:/Users/condu/source/repos/xuantoi2012/XTPdfReader/bin/UserFeedbackValidation/
dotnet bin/UserFeedbackValidation/XTPdfMergeApp.PerformanceTests.dll --user-feedback-check
dotnet bin/UserFeedbackValidation/XTPdfMergeApp.PerformanceTests.dll --feedback-real-pdf-check '<source PDF>'
dotnet bin/UserFeedbackValidation/XTPdfMergeApp.PerformanceTests.dll --adaptive-memory-check
dotnet bin/UserFeedbackValidation/XTPdfMergeApp.PerformanceTests.dll --reader-memory-regression
dotnet bin/UserFeedbackValidation/XTPdfMergeApp.PerformanceTests.dll --mupdf-migration-check
dotnet bin/UserFeedbackValidation/XTPdfMergeApp.PerformanceTests.dll --performance-settings-check
bin/MuPdfRuntime/python.exe Tests/GpuPdfium/VerifyWindowsFontFallback.py '<source PDF>' bin/UserFeedbackValidation/results/font-corrected
```
