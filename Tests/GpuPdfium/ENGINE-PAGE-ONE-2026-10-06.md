# Drainage page-one engine experiment

Only page index 0 of the supplied 01A.MB THOAT NUOC MUA - FIT_ghep.pdf was
loaded/rendered. No GUI interaction, source mutation or production-engine
replacement. PyMuPDF/MuPDF 1.28.2 and psutil 7.2.2 installed in ignored
Tests/bin/mupdf-bench-deps; PDFium DLL is the app test runtime win-x64 library.

Compare-PageOneEngines.py runs separate processes, one page handle, one thread,
three sequential cycles: width 3000 center crop A, width 6000 center crop B,
400px horizontal pan, return B, return A. Crop is always 1920x1080 pixels.
Page dimensions agree: 1191x842 points. Timings exclude hashing, bitmap copying
and saving diagnostic images. No bitmap result cache is used by either engine.
OS file caches were not flushed; these are warm-file-cache observational tests,
not a controlled cold-open benchmark. MuPDF builds occurred after PDFium.

## Results

Means of cycles 1 and 2 (milliseconds):

| Action | PDFium | MuPDF retained list | MuPDF page.get_pixmap |
| --- | ---: | ---: | ---: |
| A, width 3000 | 947.6 | 349.4 | 841.1 |
| B, width 6000 | 418.6 | 194.6 | 682.1 |
| Pan 400px | 374.3 | 187.2 | 676.6 |
| Return B | 413.8 | 193.2 | 678.5 |
| Return A | 940.7 | 349.4 | 838.0 |

MuPDF retained-list preparation: 483ms; not included in warm replay numbers.
Open/load-page observation: PDFium 618ms; MuPDF 64-68ms including Python module
import. These APIs do different amounts of deferred work, so this is not an
equivalent time-to-first-frame comparison. PDFium first A render was 1059ms;
MuPDF list preparation plus first A replay approximately 833ms, plus open.

Private committed process memory (not full reader RAM or pure engine memory):

| Mode | Retained after renders | Peak private commit |
| --- | ---: | ---: |
| PDFium | 402.5 MiB | 428.9 MiB |
| MuPDF retained list | 185.7 MiB | 242.1 MiB |
| MuPDF recreated list | 56.9 MiB | 244.8 MiB |

MuPDF retained list increased private commit by about 131MiB during preparation.
page.get_pixmap internally constructs a display list but does not retain the
same list between our calls. Its low final memory hides substantial transient
allocations and repeated interpretation work.

## Visual and methodology limits

Diagnostic images in Tests/bin/engine-page-one were visually inspected. Region,
labels, drainage lines and hatch align; MuPDF thin strokes/hatches are noticeably
lighter. Pixel identity across engines is neither expected nor established.
MuPDF page/list outputs have identical hashes at corresponding regions; repeated
renders within each mode are stable. Saved images are white-background outputs.

PDFium uses filled-white BGRA32 with annotation flag; MuPDF uses opaque RGB24
with annotations. Native AA/stroke behavior differs. An earlier RGBA MuPDF
diagnostic measured B about 197ms and pan about 191ms, suggesting RGB24 alone
does not explain the roughly twofold difference, but quality parity remains
unverified. Do not claim equal-quality speedup or CAD fidelity certification.

This is offscreen crop raster time, NOT input latency, UI FPS, full-page raster,
request cancellation, Foxit comparison, or a representative all-PDF benchmark.
Width 3000 sees more page content in the same pixel viewport than width 6000;
its higher cost does not mean lower resolution is intrinsically slower.

## Decision

Retaining an interpreted display list is a promising measured mechanism on this
specific page: approximately 2.0-2.7x faster crop raster than this PDFium setup,
with lower observed process private commit. Simply swapping to page.get_pixmap
is not a win at width 6000. Returning to an old view still costs replay time;
display lists are not bitmap caches and do not provide immediate zoom by themselves.

Next: isolated optional MuPDF backend for the same viewer pipeline, bounded
display-list cache for active pages, obsolete-request handling and retained
bitmap presentation, with CAD stroke quality checked before adoption. Do not
replace the production engine from this one-page result. Verify AGPL/commercial
licensing before integration/distribution.

References:
https://pymupdf.readthedocs.io/en/latest/coop_low.html
https://pymupdf.readthedocs.io/en/latest/displaylist.html
https://pymupdf.io/licensing
