# Predictive zoom viewport trial

Reader: bin/MuPdfPredictiveTrial/XTPdfMergeApp.exe. Original Release build and
native two-thread trial are preserved. Close the existing reader manually
before opening another trial; single-instance forwarding keeps the old engine.
No user windows were activated or closed during implementation.

Only the designated drainage page one at default layer state uses this policy.
PDFium and other files/pages retain their prior exact-resolution policy.
This build selects the successful Python/MuPDF 1.28.2 worker, not the slower
native two-thread experiment. No GPU raster path or new parallel workers added.

## Changes

- Request region rendering during zoom, bypassing the previous 24ms settling
  gate for the designated MuPDF viewport.
- Estimate render latency per page with a bounded moving average; predict
  resolution towards the user's zoom target using the existing fixed zoom rate.
- Retain 18% headroom at rest/short steps, up to 80% during longer zoom-in motion.
  Limit surplus with a 16-million-visible-pixel planning budget and existing
  full-page-width bounds. Region margins can add pixels beyond visible area.
- Accept downsampled cached crops up to 2.5x current resolution instead of the
  previous approximately +/-4% exact-size window. Cached images remain under
  the existing bounded byte cache; no unbounded bitmap history added.
- Let an overlapping in-flight crop finish during continued zoom-in, rather
  than repeatedly canceling it. Waiting obsolete requests still use the existing
  cancellation gate. Content versions/layer invalidation are unchanged.
- No fade, forced input wait or reduction of zoom speed. Existing geometry and
  atomic crop publication remain unchanged.

## Verification

Build passed. 29 MuPDF bridge/math assertions passed; 4 real-viewer planning
assertions with a deterministic fake rasterizer passed (headroom, nearby-zoom
reuse, return-view reuse, refresh at higher resolution). Offscreen test windows
use ShowActivated=false; no Computer Use.
159 default scroll-quality assertions passed.

These checks verify policy/protocol, NOT human-perceived refinement cadence.
No synchronized input-to-frame performance capture of the changed viewer yet.
Larger predicted crops can take longer to raster and use more RAM. Abrupt zoom
direction/anchor changes can still expose sharpness changes; do not promise
imperceptible refinement or 60fps full-quality raster on arbitrary CAD pages.
Next evaluation is user's live zoom/pan A/B on page one, followed by request/
presentation timing and memory measurements if predictive headroom regresses.
