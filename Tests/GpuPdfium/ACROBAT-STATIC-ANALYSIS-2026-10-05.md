# Acrobat Static Rendering Analysis - 2026-10-05

## Scope and Method

Read-only static inspection of the locally installed Adobe Acrobat 64-bit
25.001.20435. No Acrobat process was started, attached, modified or dumped.
This notes observable PE imports, module names, signed-file metadata and plain
identifier strings; it cannot prove runtime code paths or proprietary algorithms.

## Observed Module Boundary

`Acrobat.dll` (83.7 MiB) and `AdobePDFL.dll` both depend on `AGM.dll` and
`CoolType.dll`. `CoolType.dll` imports DirectWrite, consistent with a separate
text-rendering path. `AcroCEF` includes ANGLE (`libEGL.dll`, `libGLESv2.dll`),
D3D compiler and Vulkan runtime files, but this is the Chromium Embedded
Framework web-UI host; it is not evidence that the native PDF page renderer uses
that exact stack.

The native core modules did not have static D3D11/DXGI/OpenGL imports. Acrobat
does delay-load DirectDraw and the core modules import `LoadLibrary`; graphics
backend binding can therefore occur dynamically. Static imports alone cannot
identify the final API used on this machine.

## Strong Architectural Signals

`AGM.dll` and its clients contain these identifier families:

- GPU compatibility and driver gating: `CheckGPUCompatibility`, `CanUseGPU`,
  `GetGPUInfo`, `GenerateGPUFailures`.
- Image/tile cache: `GPUImage`, `TileImage`, `TileGetBuffer`, `TileGetBounds`,
  `AGMVirtualImageIteratorContainerGetTile`, `CacheCompositorImage`.
- Other bounded caches: pattern cache, color-conversion cache and transform
  cache (`GetXFormCacheSizeMax`, `SetXFormCacheSizeMax`, `FlushXFormCache`).

The installed `AGMGPUOptIn.ini` is also read-only configuration containing
Intel, AMD and NVIDIA vendor/driver rules, including NVIDIA vendor `10DE`.
Together, these are strong evidence that the native Acrobat graphics manager
chooses GPU eligibility and manages tile/image/compositor caches. They do not
reveal cache sizes, scheduling policy, rendering algorithm or whether a given
page used GPU at runtime.

`AGM.dll` and `AdobePDFL.dll` had valid Adobe Authenticode signatures at the
time of inspection. `Acrobat.dll` reported `HashMismatch`, so it was not used
as an integrity assertion; the observations above are corroborated in the
validly signed AGM and PDF library modules.

## Comparison With XTPdfReader

The public identifiers align with the design we should build, without copying
Adobe code or depending on Acrobat:

1. Probe GPU capability and maintain a robust CPU fallback.
2. Cache composited **tiles/images**, not unlimited full-page vector display
   lists.
3. Separate text, vector and image caches, each with explicit budgets and
   purge/telemetry hooks.
4. Reuse tile buffers/textures across pan and temporary zoom transforms.
5. Treat GPU backend selection as runtime policy, not as a hard dependency.

This reinforces the result from the supplied CAD files: the next XTPdfReader
prototype should be a bounded tile/compositor layer over PDFium, rather than a
large invasive parser or rasterizer fork.
