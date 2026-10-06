# Native shared-list viewport trial

Research build only, page one of the drainage PDF. Original Release/Python
configuration is preserved. The separate reader build is bin/MuPdfNativeTrial/
XTPdfMergeApp.exe with a local mupdf-trial.json selecting mupdf-native.
Close the reader manually before opening the other build: the app is single
instance. Nothing here automates windows or changes the normal installed app.

## Build

Install PyMuPDF 1.28.2 into Tests/bin/mupdf-bench-deps (includes matching MuPDF
headers, import library and mupdfcpp64.dll). Run Build-Native.ps1 using VS 18
Community C++ tools, then dotnet build MuPdfNativeWorker.csproj -c Release.
The worker does not call Python and no longer depends on MuPDFCore.
MuPDF DLL remains a third-party dependency; review its AGPL/commercial license
before any distribution. Headers/libraries/binaries stay in ignored output.

viewport.cpp opens one document and creates one page-one display list. A
locks-enabled root context and two cloned contexts share that list/store.
Each render uses the same full-page transform, clipped horizontal bands with
8px gutters, separate RGB buffers, and commits one assembled reply. Native
errors are caught inside MuPDF try/catch, never unwound across P/Invoke.
The C# worker uses the existing JSON-header/binary-pixel protocol. Source
size/mtime invalidates the session; scope/layers/cancellation/idle disposal
and PDFium fallback follow ExperimentalMuPdfViewport's existing policy.
Cancellation still drains in-flight work; mid-render abort is not implemented.

## Verification, 2026-10-06

Native DLL, native worker and separate Release reader build passed.
Scroll-quality regression suite: 159 checks passed on rerun; first run hit its
existing 3-second transient full-page retry deadline. No timing assertion or
production retry behavior was changed to make it pass.
25 bridge checks passed using mupdf-native, covering scope, immutable/exact/
nonblank/stable WPF output, cancellation, errors and session recovery.
First bridge crop approximately 940ms; warm 220/217/214ms. Worker private commit
approximately 161-188MiB (not full reader RAM).

Benchmark-MuPdfNative.py, same centered 1920x1080 page-one crop, medians of four
warm renders including IPC (first of five excluded):

| Full page width | Native 1 thread | Native 2 threads |
| --- | ---: | ---: |
| 3000 | 395ms | 333ms |
| 6000 | 265ms | 231ms |
| 12000 | 142ms | 168ms |

The Python bridge previously measured around 200-205ms at width 6000. Native
two-thread sharing therefore does NOT establish an overall speed improvement
over the successful current app. Shared-cache/allocator contention is a
hypothesis, not profiled proof. Deep zoom can regress. Keep this as an A/B trial,
not the normal default; user visual evaluation and further profiling are needed.

One vs two native threads differs in 26,301 / 48,321 / 52,227 RGB bytes at the
three widths, max channel differences 18 / 17 / 14. Native two-thread image at
width 6000 was visually inspected: content aligned, no obvious white seam.
This is not exact pixel equivalence or CAD fidelity certification.
Raw files: Tests/bin/mupdf-native-band-1282.jsonl and mupdf-native-images/.

An initial MuPDFCore 2.0.1/older-engine experiment was rejected: slower output
and much larger differences. No code from that implementation was incorporated
into the custom bridge. Its downloaded study checkout is ignored.

Public native threading rules:
https://mupdf.readthedocs.io/en/1.27.0/reference/c/overview.html
