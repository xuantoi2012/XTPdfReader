# Opt-in GPU raster compositor experiment

This experiment keeps the native MuPDF renderer and its image fidelity, metadata,
OCR, edit/save and worker protocol. A new Direct3D9Ex compositor uploads immutable
page/region images as premultiplied BGRA textures and draws them to a viewport-sized
render target exposed to WPF through D3DImage. WPF continues to draw annotations,
selection, borders and other interactive overlays.

It is explicitly **not enabled by default**. The previous WPF path is already
hardware accelerated where available; this experiment tests a separate composition
path, not an assumption that the old application was CPU-only.

## Running

Build with `-p:GpuRaster=true` to build/copy `xtgpuraster.dll` using Visual Studio
C++ x64 tools. Windows supplies the Direct3D runtime; there is no new NuGet graphics
wrapper dependency. The ordinary build does not require the GPU C++ target.

The separate preview is `Tests/bin/GpuPreviewPublish` (Reader plus XT Capture).
Close existing Reader instances, then run `Run-GPU.cmd` to enable the experiment
for that process. Running `XTPdfMergeApp.exe` directly uses the normal WPF path.
The launcher does not change machine/user environment settings. The original
`Tests/bin/NativeOpenFixPublish` remains available.

## Resource ownership and fallback

- The hardware adapter is selected for the Reader's monitor at device creation.
  The measured adapter was NVIDIA GeForce RTX 4070. Changing adapters during a
  live window move is not explicitly implemented in this prototype.
- A texture LRU is bounded to 192 MiB per presenter. At most two CPU preparation
  tasks, also bounded to 192 MiB of output, can be outstanding. The managed cache
  holds immutable source images while their textures are resident; these references
  can extend their CPU lifetime. This is additional resource use, not a RAM saving.
- Frozen bitmap conversion/copy runs in the background. WPF draws complete frames
  while textures are unavailable, so incomplete GPU scenes never replace content.
  Obsolete completed preparations are removed to avoid blocking the queue.
- Surface acquisition uses a nonblocking TryLock. A busy surface uses WPF for that
  frame. Every TryLock attempt is balanced, even on failure: WPF's implementation
  increments its lock nesting count on unsuccessful attempts too.
- Device, allocation or native-call failure releases GPU resources and selects WPF
  for the rest of that presenter. Missing DLL is handled the same way. Memory
  pressure, Start, document replacement and unload release resources as well.
- Each visible frame is still copied/composed by WPF's D3DImage interoperability.
  This is not direct worker-to-GPU or a zero-copy presentation pipeline.
- GPU sampling currently uses bilinear filtering. Integer-size/rotation/clip/alpha
  tests match WPF within one channel level; arbitrary resampling is not guaranteed
  to match WPF's high-quality filter byte-for-byte.

## Measured A/B

`--gpu-reader-bench <pdf>` opens the real 277-page P: drawing in ReaderWindow,
selects its middle page, sets manual zoom, warms each trace for 90 frames, then
measures 180 distinct CompositionTarget rendering callbacks. The trace pans and
zooms around 150%. ABBA order is WPF, GPU, GPU, WPF. It verifies actual surface
draws and GPU frames rather than counting idle callbacks as GPU presentation.

Final measured run before the unrelated closed-window menu guard:

| Mode | UI interval p50 | UI interval p95 | Intervals >25 ms | GPU-composed / surface draws |
|---|---:|---:|---:|---:|
| WPF 1 | 16.69 ms | 18.08 ms | 7 | 0 / 201 |
| GPU 1 | 16.70 ms | 32.67 ms | 13 | 109 / 245 |
| GPU 2 | 16.64 ms | 17.30 ms | 0 | 110 / 240 |
| WPF 2 | 16.67 ms | 17.49 ms | 5 | 0 / 215 |

These are UI callback intervals, not measured display scanout or GPU execution
times. Warm native caches, render completion, texture uploads and ordinary machine
load affect this short trace. CPU submit timing is not GPU timing. The mixed
results do **not** demonstrate a repeatable smoothness advantage, so the default
remains WPF. Do not interpret the best row as a general speedup.

The real-file navigation check also visited all 277 pages with the GPU option,
verified sharp viewport coverage and unchanged source size/time, and produced
534 GPU frames / 622 uploads without device failure (283 checks). The final
document rebinding step releases retained textures. Some frames intentionally use
WPF while GPU resources are busy or memory pressure is active. That run took about
54 seconds at its restored zoom; it is not comparable to the earlier 30-second
baseline which had a different restored zoom.

The 37 direct GPU checks cover pixel parity at 0/90/180/270 degrees, clipping,
opacity, cache reuse/eviction, resizing, disposal/recreation, failed-lock recovery,
asynchronous preparation and forced failure fallback. Full Reader smoke checks
exercise the integration separately. Initial GPU UI stress exposed a deferred
More-tools action firing after its window closed; the callback now checks that
its owner and button are still loaded before dispatching the action.

## Reproduction

```powershell
dotnet build Tests/PerformanceTests.csproj -c Release -p:GpuRaster=true -o Tests/bin/GpuRasterRegression
dotnet Tests/bin/GpuRasterRegression/XTPdfMergeApp.PerformanceTests.dll --test TestGpuRaster
dotnet Tests/bin/GpuRasterRegression/XTPdfMergeApp.PerformanceTests.dll --gpu-reader-bench 'P:\path\drawing.pdf'
$env:XTPDF_GPU_COMPOSITOR='1'
dotnet Tests/bin/GpuRasterRegression/XTPdfMergeApp.PerformanceTests.dll --reader-navigation-check 'P:\path\drawing.pdf'
dotnet Tests/bin/GpuRasterRegression/XTPdfMergeApp.PerformanceTests.dll --ui-smoke
Remove-Item Env:XTPDF_GPU_COMPOSITOR
```

There is no multi-hour GPU soak or broad GPU/driver/RDP qualification. This is a
reviewable experiment for the owner's trial, not a replacement for the native UI
development baseline.

## Re-run on the 93 MB drainage drawing (T:, 2026-10-10, same build `GpuFinalRegression`)

`--gpu-reader-bench` with `XTPDF_NATIVE_WORKER=1`, ABBA, RTX 4070, 180 frames per trace:

| Mode | Interval p50 | p95 | >25 ms | App CPU / 3 s trace | UI draw CPU (180 frames) | GPU-composed / drawn |
|---|---:|---:|---:|---:|---:|---:|
| WPF 1 | 16.68 | 18.14 | 1 | 1672 ms (cold) | 4.58 ms | 0 / 186 |
| GPU 1 | 16.73 | 18.29 | 1 | 969 ms (cold) | 26.34 ms | 95 / 190 |
| GPU 2 | 16.78 | 18.63 | 4 | 375 ms | 26.62 ms | 90 / 190 |
| WPF 2 | 16.69 | 18.66 | 1 | 359 ms | 3.97 ms | 0 / 186 |

Both modes are locked to the 60 Hz vsync (p50 16.7 ms), so interval cannot show a gain. UI-thread draw
cost is about 6x higher with the compositor, and only about half of the frames were GPU-composed
(the rest fell back to WPF). Warm app CPU is equal (375 vs 359 ms). No benefit measured; default stays WPF.
