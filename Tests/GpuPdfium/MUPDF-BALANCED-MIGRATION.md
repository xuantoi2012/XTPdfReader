# Balanced MuPDF backend (2026-10-06)

Local executable: `bin/MuPdfBalanced/XTPdfMergeApp.exe`. Close the old reader
manually before launching it because the application is single-instance.
No live user window was automated or closed during this work.

## Selected balance

Keep four isolated processes (two visible, two background) and the reusable
4608-pixel whole-page tier that the user found smooth. Reduce retained memory:

- WPF bridge raster LRU: 768 MiB, down from 2048 MiB.
- Reader page cache: 768 MiB, down from 2048 MiB.
- Region cache: 256 MiB, down from 1024 MiB.
- Native worker: two LRU documents and eight display lists per process.
- No duplicate Python raster cache: the frozen WPF image already owns the result.
- Background processes retire after 120 seconds idle; visible processes after
  30 minutes. Cache hits still work without a live worker.
- Closing files releases worker documents, display lists and bridge images.
- Source-file stat calls on network drives run off the WPF frame thread.

Caches may share references to the same bitmap; their budgets are not additive
physical-RAM measurements. Native resources, annotation caches, WPF and transient
buffers are additional memory. This is not a hard cap on total application RAM.

Same two-file background sequence as the throughput trial, including four
uncached 1201x801 crop requests:

| Metric | Throughput trial | Balanced trial |
| --- | ---: | ---: |
| Four-crop batch | 242 ms | 247 ms |
| Sum worker private commit | 985 MiB | 717 MiB |
| Parent private commit | not recorded | 777 MiB |
| Bridge retained bitmaps | not recorded | 269 MiB |

Worker private commit decreased about 27%. This is not a claim of 27% lower
total RAM or a controlled maximum-speed benchmark. User activity continued;
OS caches were not flushed. Drainage page one first whole-page render was about
1.32 seconds; cached revisit about 24 ms. First-render timings still vary.

## Migration

`MuPdfOnly=true` is now the default project setting. It excludes the PDFium
NuGet runtime and DLL-copy targets, and compiles the MuPDF-only policy. A fresh
balanced output contains no PDFium DLL and its deps.json has no PDFium entry.
The existing PDFium code remains for explicit regression builds with
`-p:MuPdfOnly=false`; it is not a fallback in the new default reader.

MuPDF now handles metadata, dimensions/rotation, raster pages and crops,
thumbnails, layers by OCG identity, annotation-inclusive print bands,
transparent in-memory annotation rendering, widgets, passwords, selection and
search. Editing/export/security still use the existing iText services.
Unsigned signature widgets with AP streams need a render-only compatibility
adaptation; changes are never saved back to the source PDF.

MuPDF crops use coherent replacement, not the legacy BGRA strip compositor:
the latter produced antialias differences up to 20/255 when forced on this
engine. The accepted whole-page/atomic-region presentation remains unchanged.
Nearby zooms reuse an existing bitmap; deeper zoom still requires new raster
work and cannot promise indefinitely sharp cached images.

## Verification And Runtime

86 migration/lifecycle checks, 36 real heavy-file/render/cache checks, and four
offscreen WPF nearby-zoom checks passed on the DLL-free build. No PDFium module
was loaded by tested paths. 159 legacy scroll checks passed separately with
`MuPdfOnly=false`; that engine-specific suite is not claimed to pass on MuPDF.
Physical printer output and every user PDF were not tested.

The executable has its own 95.8 MiB Python 3.12 / PyMuPDF 1.28.2 runtime and
worker script. It does not need the Codex runtime paths. Build inputs are local
ignored `bin/MuPdfRuntime` files; recreate them using
`Packaging/Prepare-MuPdfRuntime.ps1` with compatible runtime/package sources.
Dependency license files are included; review distribution terms before release.

Layer APIs: https://pymupdf.readthedocs.io/en/latest/document.html
