# Provided CAD Files - GPU Fork Probe

## Scope

Two supplied PDFs were read only. SHA-256 before and after the profiling run
matched, so neither input was modified. The native probe ran below normal
priority with a confirmed NVIDIA GeForce RTX 4070 ANGLE/D3D11 renderer.

This is a diagnostic run, not an application FPS claim: it is offscreen and
excludes WPF composition, presentation and input latency. Each value is the
median of five measured frames after three warmups from a fresh process unless
noted otherwise. The GPU call waits for completion.

| File / Page | Recorded ops | Picture estimate | Direct CPU | Picture CPU | Picture GPU | Direct GPU | Texture transform |
|---|---:|---:|---:|---:|---:|---:|---:|
| `2.BinhDo.pdf` / 1 | 1,411,445 | 111.5 MiB | 236.1 ms | 73.0 ms | 86.8 ms | 259.0 ms | 0.064 ms |
| `2.BinhDo.pdf` / 2 | 1,233,263 | 96.9 MiB | 224.7 ms | 69.8 ms | 69.9 ms | 222.3 ms | 0.050 ms |
| `2.BinhDo.pdf` / 3 | 2,266,338 | 181.5 MiB | 588.0 ms | 339.5 ms | 373.0 ms | 603.5 ms | 0.208 ms |
| `2.BinhDo.pdf` / 4 | 644,647 | 50.5 MiB | 126.7 ms | 42.9 ms | 37.5 ms | 113.2 ms | 0.096 ms |
| `01A.MB THOAT NUOC MUA - FIT_ghep.pdf` / 1 | 4,562,927 | 354.5 MiB | 892.2 ms | 466.7 ms | 670.1 ms | 1042.2 ms | 0.131 ms |
| same / 11 | 405,064 | 31.6 MiB | 99.4 ms | 58.2 ms | 71.8 ms | 109.7 ms | 0.121 ms |
| same / 61 | 23,311 | 2.0 MiB | 16.3 ms | 12.5 ms | 8.4 ms | 11.5 ms | 0.074 ms |

`Picture GPU` is replaying the cached PDFium display list. `Direct GPU` asks
PDFium to traverse and draw the PDF again for every frame. `Texture transform`
is compositing an already-rendered GPU image and is a preview-interaction cost,
not the cost of rendering new content.

The fork API was compiled and exercised on the two stressed rows, `BinhDo` page
3 and drainage page 1. The remaining rows were captured immediately before the
API was moved into PDFium, using the identical `FPDF_RenderPageSkia` to R-tree
`SkPicture` recording/replay sequence. They are cache-admission diagnostics,
not a claim that the small API wrapper independently changes raster speed.

## Findings

The two files are CAD/vector workloads, not primarily large-bitmap workloads.
The visual samples contain dense hatches, repeated symbols and linework. The
heaviest pages record 2.27 to 4.56 million operations. On those pages, direct
GPU is no faster than CPU and can be slower. Recording helps, but a whole-page
picture itself costs 181-355 MiB and still takes 408-670 ms to replay on GPU.
It must never be admitted as an unrestricted per-tab cache.

The visual pixel checks were nonblank. On the inspected map and drainage-plan
viewports, labels, hatch and linework remained present; GPU differences were
limited to antialiasing. The fork API records and replays correctly, but that
does not yet validate annotations, form fields, optional content or every zoom.

## Implementation Decision

For dense CAD pages, cache **raster tiles**, not the entire display list:

1. Divide the page into GPU textures at a limited set of zoom buckets.
2. During pan or continuous zoom, transform existing visible tiles immediately.
3. Render newly exposed or sharper tiles asynchronously, ordered by viewport
   direction; cancel obsolete work.
4. Use whole-page `SkPicture` only when its operation and byte estimates pass
   a strict admission threshold. Otherwise use it transiently while rendering
   a tile, then destroy it.
5. Share one GPU context across tabs. Bound active-tab textures and evict
   inactive-tab tiles first. A 64 MiB Skia resource-cache limit is a starting
   point, not a process-memory limit.

The custom PDFium API created in this probe is deliberately small:
`FPDF_CreateSkiaPicture`, `FPDF_RenderSkiaPicture`, diagnostics and destroy.
It is compiled only with `PDF_USE_SKIA`, preserves the standard renderer, and
is kept as a patch on top of the pinned upstream commit. The next milestone is
a native tiled render host that presents GPU textures without copying each frame
back to a WPF bitmap.
