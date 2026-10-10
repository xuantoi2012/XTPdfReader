# Native baseline for UI development

The current engine is the baseline for the next UI work. Keep rendering, worker
transport, cache budgets, cancellation and PDF save semantics stable during UI
changes. Fix reproducible engine defects when found; additional architecture or
performance experiments should have their own measurements and regression run.

Implemented paths include native viewing, page metadata, layers, text/search,
OCR and text/object editing. Shared pixel transport, bounded caches, native
cooperative cancellation and the initial Reader binding fix are included.
This is a development baseline, not a claim that every PDF has been validated.

## Reproducible validation

`Tests/Test-NativeRelease.ps1` builds and runs the native integration suite,
cache fidelity, cancellation, cache lifetime, edit/save A/B, OCR A/B, migration,
Reader memory/binding and full UI smoke checks. It stops on failure and saves logs
in `Tests/bin/NativeReleaseGate/validation`. Use `-RealPdf <path> -AllPages` to add
the actual Reader navigation regression for an owner document.

Final validation completed successfully on 2026-10-10:

| Check | Result |
|---|---|
| Release build | 0 errors; 25 existing warnings |
| Native installation, metadata, shared pixels, timings and OCR integration | 131 checks passed |
| Cache fidelity, edit/save parity and four OCR A/B scenarios | Passed |
| Native cancellation and recovery | 38 cancelled frames; handles 64 → 64 |
| Native lifetime | 256 renders; private memory 42.5 MiB after each of four sweeps |
| Migration | 91 checks passed |
| Reader memory / initial binding | 71 / 8 checks passed |
| Full UI smoke | 681 checks passed |
| Real 277-page Reader navigation | 282 checks passed |

The release gate itself finished with exit code 0. The real-file navigation was
run separately with the same final application source before the gate.

The 277-page, 170,261,791-byte drawing on P: was opened through the actual Reader
and visited in alternating first/last order, covering every page. The check uses
the viewer's actual sharp-frame readiness logic, accepting full-page images or
native regions as appropriate; it does not mistake a preview for a sharp frame.
All 282 checks passed in approximately 30 seconds on the measured warm run.
Source size and modification time stayed unchanged. This is a short navigation
stress run, not a cold-network benchmark or a multi-hour soak.

In that navigation run, sampled app private memory was approximately 887–1191 MiB
and was 1010 MiB at the last sample. These are app-process samples, not total RAM
across the worker processes, and do not establish a universal memory ceiling.
The separate generated worker lifetime check covers bounded caches and repeated
render memory stability.

## Deliberately deferred optimizations

- Replacing WPF composition or adding a GPU backend needs workload measurements
  and a separate implementation; it is not required to start UI work.
- Custom MuPDF builds, tile tuning and extra workers need evidence of a remaining
  bottleneck. Current fidelity and cancellation behavior take priority.
- `storeBytes` in native stats is a placeholder; it must not be shown as a measured
  MuPDF store size. Current memory control samples worker private memory and shared
  mapping capacity instead.

No multi-hour soak, installer qualification or release upload is implied.
The runnable baseline is `Tests/bin/NativeOpenFixPublish/XTPdfMergeApp.exe`,
including XT Capture and requiring .NET 10 Desktop Runtime. No commit or push
has been made for the current local changes.
