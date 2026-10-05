# PDFium / Skia GPU experiment

Standalone, offscreen native benchmark. It does not replace the application's
renderer or interact with the desktop. The process runs below normal priority.
No commercial SDK, Foxit binaries, or proprietary code are used.

## Fixed dependencies

- PDFium chromium/7690: `7cfa57a91284ba907cb0facaf9cd8e4a570fe5c7`.
- Skia: the revision fetched by that PDFium DEPS, not a separately linked SkiaSharp.
- ANGLE EGL/GLES DLLs: official `SkiaSharp.NativeAssets.WinUI` NuGet 4.153.1,
  `runtimes/win-x64/native`. Preserve its LICENSE and THIRD-PARTY-NOTICES when
  distributing binaries. No native binaries are committed here.
- Local compiler: Chromium's fetched clang toolchain, VS 2026 C++ headers/libs,
  Windows SDK 10.0.26100.0.

Sources: [PDFium build](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/README.md),
[SkPictureRecorder](https://api.skia.org/classSkPictureRecorder.html),
[ANGLE](https://github.com/google/angle/blob/main/README.md).

## Build

The prepared external checkout is under `repos/tmp/pfgpu`, outside the app repo.
`Build.ps1` checks the source revision, applies the local fork patch when needed,
copies this benchmark into `xt_gpu_poc`, copies `args.gn`, generates GN, builds
with four workers and stages ANGLE DLLs. Paths can be overridden with its
parameters. It only changes process environment and the external PDFium checkout.

For a fresh checkout, clone depot_tools and PDFium, then run `gclient sync` with
the solution URL pinned to the SHA above, `checkout_v8=false`,
`checkout_configuration=small`, and `target_os=['win']`. Also fetch
`third_party/simdutf` at `f7356eed293f8208c40b3c1b344a50bd70971983`:
this revision's GN test graph references it even when V8 is disabled.
Add `"//xt_gpu_poc:xt_gpu_poc"` to the external root BUILD.gn default group's
deps. Set `DEPOT_TOOLS_WIN_TOOLCHAIN=0` and `GYP_MSVS_OVERRIDE_PATH` to the actual
VS installation. Use `buildtools/win/gn.exe` directly and an actual Python 3
executable on PATH, not a Windows Store alias.

```powershell
.\Tests\GpuPdfium\Build.ps1
.\Tests\GpuPdfium\Run.ps1
# A GPU-only cache/stress run:
& '<external>\out\GpuPoc\xt_gpu_poc.exe' '<input.pdf>' 30 '<output-prefix>' 300 64 gpu-only

# Supplied-file suite; inputs are mandatory and remain read-only.
.\Tests\GpuPdfium\RunProvidedFiles.ps1 -BinhDo '<input.pdf>' -ThoatNuocMua '<input.pdf>'
```

`Run.ps1` uses the existing local source manifest, pages 11 and 30 of the two
large PDFs, three fresh processes per case, and 30 measured frames per mode.
It verifies input SHA256 before/after. Results and reference/GPU PPM files go
under ignored `Tests/bin/gpu-poc`. No input PDF is edited.

## What Is Measured

1. Document/page loading, then the XTPdfReader PDFium fork records page content
   into an R-tree SkPicture through `FPDF_CreateSkiaPicture()`.
2. Direct PDFium-to-CPU Skia, picture-to-CPU, picture-to-GPU and direct PDFium-to-GPU.
3. Transforming an already-rendered texture, as an interaction preview.

Viewport: 1280x800; initial fit-width multiplied by three; deterministic pan/zoom.
Long runs repeat the same 30-transform cycle. Three warmups precede measured
frames. GPU calls use `flushAndSubmit(GrSyncCpu::kYes)` to include completion,
not just command submission. Readback and image writing are outside timings.
An exact NVIDIA adapter LUID is selected; software D3D11 is rejected.

## Important Limits

- These are native offscreen completion times, **not WPF frame rates**. They do
  not include swapchain presentation, WPF composition, cross-device copies,
  hit testing, tab scheduling or input latency.
- CPU baseline is this same Skia build, not the existing app's bundled renderer.
- Public experimental `FPDF_RenderPageSkia` renders page content without an
  annotation flag. Annotation/form/layer behavior requires separate validation.
- Recording at page-point dimensions can bake raster fallbacks at that scale.
  Image-rich/transparency-heavy documents need multi-scale quality tests before use.
- Texture preview cannot reveal content outside its cached area or add detail at
  higher zoom. It needs background tile refinement and viewport coverage handling.
- `picture_approx_mb` excludes referenced images/fonts. `skia_gpu_cache_mb` is
  Skia-tracked GPU resources, **not total VRAM**. Process private bytes include
  driver allocations and all modes executed in that process. Cache limit is not
  a hard process-RAM limit; live resources can exceed it.
- Pixel checks cover one transform per page. CPU recording replay matched the
  direct CPU image exactly in the measured cases; GPU antialiasing differs.
  Visual samples were inspected but this is not a whole-document fidelity proof.

See [RESULTS-2026-10-05.md](RESULTS-2026-10-05.md) for the measured decision.
The small fork patch is [0001-skia-picture-api.patch](pdfium-patches/0001-skia-picture-api.patch).
Results for the supplied CAD files are in [PROVIDED-FILES-2026-10-05.md](PROVIDED-FILES-2026-10-05.md).
