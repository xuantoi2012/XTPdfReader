# Foxit PhantomPDF static analysis (2026-10-05)

## Scope and method

This is a read-only static inspection of the locally installed Foxit
PhantomPDF 10.0.0.35798. It examined PE imports, delay-load imports, module
layout, public strings, and configuration files. It did not attach to, alter,
or debug the user's running Foxit process; it also did not disassemble or
attempt to reproduce proprietary code.

Static inspection can establish which graphics stacks are shipped or linked,
but cannot prove a runtime scheduling policy or cache size.

## Modules examined

* `FoxitPhantomPDF.exe` is the 32-bit host application.
* `FPCSDK.dll` is the 32-bit signed Foxit SDK component. `FPCSDK64.dll` is
  also present for 64-bit helper/plugin workflows.
* `plugins/PDFA/pdfEngineLight.dll` (and its x64 counterpart) belong to the
  PDF/A plugin. Their TIFF-like `tile` strings describe image decoding and
  output tiles, not evidence of a viewer tile cache.
* Chromium/compositor modules are shipped under the HTML-to-PDF Creator
  plugin. They are unrelated to the native document viewer and must not be
  used as evidence that page painting uses Chromium GPU compositing.

## Findings

The native application and SDK import `GDI32`, `MSIMG32`, and `GDI+` for
graphics. Their normal and delay-load import tables contain no `D3D9/10/11/12`,
`DXGI`, `OpenGL`, `Vulkan`, `D2D1`, or `DWrite` library. No corresponding
graphics runtime DLL is shipped next to the main native executable.

The main executable contains ordinary GDI+ calls for paths, images, clipping,
fonts, and bitmap locking. `FPCSDK.dll` likewise exposes no public
GPU-rendering symbol. The few readable `GPU`, `GL`, and `angle` byte strings
in the inspected modules are isolated or look like unrelated third-party/data
content; they are not coupled to graphics API imports.

The application has a `pdfEngineLight.dll` import entry. On this installation,
the actual matching DLL resides under the PDF/A plugin, which indicates a
plugin or optional workflow, not conclusive evidence that the primary reader
uses a separately GPU-backed PDF engine.

## Conclusion for XTPdfReader

There is no static evidence that this Foxit 10 installation's native viewer
achieves its responsiveness by GPU-rendering every PDF operator. Its visible
pipeline is compatible with a CPU PDF renderer producing bitmaps, followed by
efficient on-screen composition and cache reuse.

That is aligned with the benchmarked direction for XTPdfReader:

1. Render only visible PDF tiles on worker threads; prioritize the viewport.
2. Keep a bounded CPU bitmap cache and a bounded GPU texture cache per active
   document, evicting by recency and zoom level.
3. During pan or live zoom, transform already-present textures immediately;
   schedule crisp replacement tiles after the gesture settles.
4. Reuse thumbnails and nearby-page tiles, but never retain whole-page display
   lists for dense CAD sheets.
5. Use one shared D3D11/ANGLE composition device rather than one renderer per
   tab, with an explicit memory budget.

For the supplied CAD PDFs, the earlier prototype showed that a complete Skia
picture consumes roughly 182 MB to 355 MB per dense page and still requires
hundreds of milliseconds to build. A bounded raster-tile cache is therefore
the practical way to match the perceived smoothness while keeping RAM under
control. GPU composition remains valuable for pan/zoom, but it does not remove
the PDF parsing and vector rasterization bottleneck.

## Next validation

The implementation should be evaluated with a small telemetry panel or log:
visible-tile latency, first-sharp-frame latency, cache hit rate, CPU/GPU cache
bytes, dropped/obsolete render jobs, and UI-frame duration. These runtime
metrics will tell us whether the Foxit-like behavior is reached much more
reliably than further static inspection can.

## Read-only runtime study plan

The goal is to learn the observable pipeline, not copy Foxit code. Run the
same scripted sequence (open, zoom in three notches, pan, zoom out, revisit a
region) against Foxit and XTPdfReader and capture:

1. **ETW/WPA**: process/thread CPU time, context switches, disk reads, GPU
   engine activity, and the time from input to the first changed frame.
2. **ProcMon**: file and temporary-file activity during the first view and a
   revisit. This can distinguish a persistent tile cache from a RAM-only
   cache.
3. **WinDbg (observation only)**: thread count and sampled call stacks while
   idle, zooming, and settling. Look for render-worker groups and whether the
   same page is processed by independent native workers.
4. **PE/import inspection**: record graphics and image APIs, but do not treat
   a string or an unrelated plugin as proof of the viewer's runtime path.

The useful comparison is behavioral: viewport-first scheduling, cancellation
of obsolete work, cache hits on revisit, and atomic replacement of a coherent
composite. The trace cannot establish Foxit's private algorithm or justify
reusing proprietary code; it can tell us which scheduling and cache contracts
our implementation should reproduce.
