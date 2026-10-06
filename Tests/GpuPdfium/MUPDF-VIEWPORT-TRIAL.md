# Optional MuPDF viewport bridge

The local Release build is now enabled by bin/Release/net10.0-windows10.0.19041.0/
mupdf-trial.json. Open that EXE normally; no launcher/environment setup is
required. Configuration uses this machine's worker/Python paths and stays in
ignored build output, not a shipping package. Removing the configuration and
restarting restores PDFium. Environment settings override local configuration.
The worker package directory can be set with XTPDF_MUPDF_PACKAGES without
changing the parent application's Python environment. Configuration loading
without environment variables passed all 25 bridge checks.

Research-only backend, disabled by default. Does not bundle MuPDF in app output
or replace PDFium. AGPL/commercial licensing needs review before distribution.

## Scope

ContinuousPdfView region rendering can opt in via XTPDF_EXPERIMENTAL_ENGINE=mupdf.
Only page one of XTPDF_MUPDF_SOURCE at default layer visibility is eligible.
Other documents/pages, overridden layers and custom test renderers use PDFium.
Full-page previews, thumbnails, metadata and single-page viewer remain unchanged.

Persistent Python worker holds one document and one interpreted display list.
MuPDF raster stays off the UI thread. Binary RGB24 output becomes an immutable
WPF bitmap; existing viewport compositing, bitmap reuse and stale-result checks
still apply. File size/mtime invalidate the worker's list on the next request.
Worker stops after 60 seconds idle, on protocol errors, or application exit.
Errors fall back to PDFium and disable this experimental route for the process.

Cancellation removes waiting requests before dispatch. An already executing
native render is drained before its obsolete result is discarded, preserving
protocol alignment and the display list. This is NOT mid-render interruption;
rapid zoom may still wait for that render, typically about 200ms in this test.
Worker I/O has a 30-second watchdog that kills a stalled worker.

## Run

Build Tests/PerformanceTests.csproj (also builds the reader). Install isolated
PyMuPDF 1.28.2 and psutil 7.2.2 into Tests/bin/mupdf-bench-deps using the bundled
Python runtime. Start-MuPdfViewportTrial.ps1 sets process-local variables and
restores them afterward. Run with -Check for background verification.

For interactive testing, close the existing reader manually and run the script
without -Check. It opens only the supplied drainage file. It deliberately refuses
to forward into an existing single-instance reader because that process would
retain its previous environment/engine configuration. No existing windows are
automated or closed by this script. Restart normally to return to PDFium.

## Verified on 2026-10-06

Build passed with existing UiSmokeTests.cs:117 nullable warning.
Bridge checks: 25 assertions, covering scope, immutable/nonblank/exact/stable
bitmaps, cancellation/recovery, worker-error disposal/disable, and restart.
Default viewer regression suite: 159 scroll-quality assertions passed.

Measured direct bridge crop on actual drainage page one (1920x1080, full width
6000): first render including startup/list build 813ms; warm repeats 206, 203,
208ms. Worker private memory approximately 176MiB; test parent 35-55MiB.
These figures include transfer and WPF bitmap construction but not actual
reader input-to-frame latency. Production reader still holds PDFium preview
state, so total app memory may INCREASE with both engines active. Do not use the
isolated benchmark memory saving as a whole-app claim.

No GUI smoothness comparison was performed in this background-only turn.
Thin hatch/stroke rendering differs from PDFium and still needs CAD quality
review. Follow-up should compare presentation cadence and cancellation lag,
not just average raster time. This bridge intentionally measures IPC overhead
before investing in a native C/C++ binding.
