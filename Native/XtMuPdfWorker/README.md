# Native MuPDF worker

The default `MuPdfOnly` build now uses a native worker for viewing, text/search,
OCR and text/object edits. The installed application needs no Python runtime.
Viewing uses a persistent isolated process; OCR/edit jobs use separate processes.
Replacement text and the OCR text layer are still written by the existing C# writers.

## Build

Requires Windows x64, .NET 10 SDK and Visual Studio C++ x64 Build Tools.
The tested SDK/DLL pair is PyMuPDF 1.28.2. To prepare build/test dependencies:

```powershell
python -m pip install --target Tests/bin/mupdf-bench-deps pymupdf==1.28.2
& Native/XtMuPdfWorker/Build.ps1
dotnet build XTPdfMergeApp.csproj -c Release
dotnet publish XTPdfMergeApp.csproj -c Release -r win-x64 --self-contained false -p:NativeOnly=true -p:DeployToBundle=false -o Tests/bin/NativeOnlyPublish
```

The project rebuilds the worker when native sources change. `XTPDF_MUPDF_SDK`
or `Build.ps1 -Sdk` can specify another SDK directory; its parent must contain the
matching `mupdfcpp64.dll`. Do not mix SDK headers/import library and runtime versions.
The default release script includes the native worker and Vietnamese tessdata.
`Packaging/Release/Build-Release.ps1 -WithPython` or `-p:NativeOnly=false`
keeps the legacy Python workers/runtime available for comparison.

Use a fresh output directory when switching build modes; MSBuild does not remove
old Python content from an existing output directory.

## Protocol and memory

No arguments: JSON lines on stdin/stdout. A render reply's JSON header is followed
immediately by its BGR/BGRA bytes unless it contains `sharedMemory` (see below).
`metadata` supports `data: {countOnly:true}`
and zero-based `data: {first:0,count:16}`. Returned `count` is the document total,
`first` is the subset start, and `sizes` contains only the requested range.
The reader requests the selected size first and reads other sizes in batches of 16.

`--ocr job.json` and `--textedit job.json` retain the legacy job/message schema.
Jobs report errors on stdout and fatal errors exit nonzero. Read-only requests do
not save. Edits remain pending in the application until Save, as before.

The app requests `sharedMemory:true` for native viewing. With worker raster caching
disabled, replies can contain `sharedMemory` (a Windows mapping name) and
`sharedCapacity`. Such replies have no binary stdout payload. WPF copies from the
mapping while holding the worker gate; **only one request may be outstanding per
worker**, and the client must finish using the mapping before sending another.
Allocation failure or enabled worker raster caching falls back to the binary reply.
One mapping per worker is bounded to 256 MiB, reused for nearby sizes, shrunk when
oversized, and removed on close/release/memory commands and cancelled renders.
The memory controller includes retained mapping capacity as well as process private
bytes. The shared path has no intermediate managed pixel array.

Native render requests may include a request-scoped Windows event name
`cancelEvent` with prefix `Local\XTPdfCancel-`. The event's signal aborts MuPDF's
cookie during display-list creation/rasterization. Windows' wait pool observes it
without a polling loop. Incomplete lists and partial frames are discarded; the
error reply is drained and the same worker can serve subsequent requests.
Individual MuPDF operations and synchronous file I/O can still delay cancellation.

Default budgets are **64 MiB of file blocks** and **128 MiB of raster pixels per
worker**, plus the display-list cache and MuPDF store. The application has its own
bitmap cache; its performance profile may disable worker raster retention.
`stats` reports block/raster/shared bytes, cache hits/misses, shared frames and native
cancelled renders; `storeBytes` is still a placeholder.

Environment switches:

- `XTPDF_BLOCK_KB`: block size, 4–4096 KiB (default 256).
- `XTPDF_BLOCK_BUDGET_MB`: block cache budget, 0–512 MiB.
- `XTPDF_FORCE_BLOCK_STREAM=1`: exercise network-style block reading on local files.
- `XTPDF_MUPDF_RASTER_MB`: raster budget, 0–512 MiB.
- `XTPDF_MUPDF_NO_RASTER_CACHE=1`: disable worker raster retention.
- `XTPDF_MUPDF_SHARED_MEMORY=0`: app uses binary pipe transport for comparison.
- `XTPDF_NATIVE_CANCEL=0`: app drains an obsolete render without native interruption.
- `XTPDF_NATIVE_WORKER=0`, `XTPDF_NATIVE_OCR=0`, `XTPDF_NATIVE_TEXTEDIT=0`:
  force the respective legacy route (requires a build with Python installed).

Memory pressure clears raster retention, trims block budgets, and limits display
lists. An active stream pins one block because MuPDF can still hold pointers into
it; this block may remain even at a zero budget. Close/release removes document
blocks, lists and rasters. File size/time changes invalidate cached documents.

## Regression checks

Run the complete native release validation from the repository root:

```powershell
& Tests/Test-NativeRelease.ps1
# Optional: include every page of an owner's PDF through the real Reader.
& Tests/Test-NativeRelease.ps1 -RealPdf 'P:\path\drawing.pdf' -AllPages
```

This stops on a failed check and retains per-check logs under
`Tests/bin/NativeReleaseGate/validation`. It requires the development SDKs and
Python/PyMuPDF for A/B checks; the published application remains Python-free.
The all-page Reader check verifies viewport sharpness using the same readiness
logic as presentation, including native region images at deep zoom.

Python is needed only to run A/B tests. These scripts create temporary fixtures;
save comparisons operate on temporary copies.

```powershell
python Native/XtMuPdfWorker/test_cache.py
python Native/XtMuPdfWorker/test_edit.py
python Native/XtMuPdfWorker/test_lifetime.py
python Native/XtMuPdfWorker/test_cancel.py
dotnet build Tests/PerformanceTests.csproj -c Release -o Tests/bin/NativeOnlyRegression
dotnet Tests/bin/NativeOnlyRegression/XTPdfMergeApp.PerformanceTests.dll --test TestNativeOnlyInstallation,TestNativeMetadataAsync,TestOcrEndToEnd
python Native/XtMuPdfWorker/test_ocr.py Tests/bin/NativeOnlyRegression/results/ocr/scan.pdf
dotnet Tests/bin/NativeOnlyRegression/XTPdfMergeApp.PerformanceTests.dll --mupdf-migration-check
dotnet Tests/bin/NativeOnlyRegression/XTPdfMergeApp.PerformanceTests.dll --reader-memory-regression
dotnet Tests/bin/NativeOnlyRegression/XTPdfMergeApp.PerformanceTests.dll --ui-smoke
dotnet Tests/bin/NativeOnlyRegression/XTPdfMergeApp.PerformanceTests.dll --test TestNativeSharedTransportAsync,TestNativeRenderTimingsAsync
```

`test_edit.py <real.pdf>` compares read-only text/object results at the first,
middle and last pages and verifies source size/time is unchanged.
`compare.py <real.pdf>` compares viewing, layers, selection and search.
The full legacy `--background-regression` also requires PDFium and is not a
native-only validation command. See the dated plan for results and remaining limits.

`bench_render.py` compares uncached rendering in two worker binaries, validates
pixel hashes, and exports p50/p95 timings. See
[performance measurements and targets](../../docs/NATIVE-PERFORMANCE-2026-10-10.md)
for the direct-buffer/shared transport optimizations, native cancellation and results.
