# MuPDF worker: pixel format, header reads, search (2026-10-07)

Measured with `dotnet <tests>\XTPdfMergeApp.PerformanceTests.dll --mupdf-ipc-bench <pdf> <page>` (new, `Tests/MuPdfIpcBench.cs`; page 31 of the 165 MB CAD file; each iteration uses a slightly different size so the raster cache cannot answer) and `searchcmp.py`-style comparisons of the worker's `search` and new `searchrange` ops.

## What changed
- **BGR from the worker.** MuPDF writes `DeviceBGR` directly (`fz_device_bgr`): WPF's byte order, no per-pixel channel swap in Python (the alpha path used to copy the whole buffer and swap two slices), and the render itself is slightly faster (35.4 -> 29.9 ms for 1376 px). The C# side builds `Bgr24` (was `Rgb24`) / `Pbgra32`. Bytes are identical to the old RGB output with channels swapped (checked on a 1200 px page, with and without alpha). WPF converts non-native formats on first draw: a 4608x3255 image drawn into a 1376x973 target took 46 ms as Rgb24, 36 ms as Bgr24, 19 ms as Bgr32 (RenderTargetBitmap, first draw). Bgr24 keeps RAM unchanged; Bgr32 would remove the remaining ~17 ms at +33% RAM and a conversion pass on a worker thread - not done.
- **Header read in chunks.** `ReadHeaderAsync` read one byte per awaited call (about 150 calls per render). It now reads blocks and hands the bytes that follow the newline to the pixel read.
- **Search.** `searchrange` op: 8 pages per round trip, and the worker uses cheap plain-text extraction (whitespace/case-insensitive containment) as a filter, so only pages that can match pay for the per-character `rawdict` boxes. The filter switches itself off once 60% of the pages seen match (then it is pure overhead). Results are identical to the old per-page `search` op on two real PDFs (158 and 123 pages, six queries each, 0 differing pages).

## Numbers
| | before | after |
|---|---|---|
| search, rare word, 158-page text PDF | 11.3-13.3 s | 0.3-0.4 s |
| search, word on every page, 123-page PDF | 1.1-1.2 s | 1.0-1.1 s |
| search, word on ~half the pages, 158 pages | 11.7 s | 7.7 s |
| render round trip, full page 4608 px | 112.2 ms | 110.5 ms |
| render round trip, 1376x781 crop | 7.8 ms | 7.3 ms |
| render round trip, 340 px thumbnail | 21.9 ms | 19.9 ms |

Conclusion: pipe transfer and copies are not the bottleneck (the render is); the real wins are search and the WPF first-draw conversion. Not changed: `Rgb24`-style full-buffer copies in Python for cached rasters (cache is off in Balanced mode), the AGPL question, worker lifetime (Job Object).
