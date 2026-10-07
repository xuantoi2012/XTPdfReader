# Virtual printer test results - 2026-10-07

Phase 0 only; see VIRTUAL-PRINTER-SPIKES.md and Tests/VirtualPrinter/README.md for measurements, versions and commands. Native Word export is not counted as an XT Reader printer/add-in/shelf pass.

| Sample | Windows 11 XT Reader acceptance | Windows 10 XT Reader acceptance | Reason |
| --- | --- | --- | --- |
| W1 full 120-page Word | not run | not run | Full fixture absent; no XT queue/add-in; no Win10 host |
| W2 3-page Word | not run | not run | No XT queue/heuristic |
| W3 300-page Word | not run | not run | No XT queue/service |
| X1 Excel | not run | not run | No XT queue |
| P1 PowerPoint | not run | not run | No XT queue |
| B1 Chrome | not run | not run | No XT queue |
| B1 Edge | not run | not run | No XT queue |
| N1 Notepad | not run | not run | No XT queue |
| M1 image / Paint | not run | not run | No XT queue |
| CAD three layouts | not run | not run | No provided DWG/cloned PC3; runtime event unverified |

For all rows: shelf arrival deadlines, default/landscape/range/copies/grayscale variants, metadata/naming, three consecutive jobs with Reader closed/open, heuristic precision/recall, W3 performance/service memory, cancel/crash/two-user recovery, LAN rejection, install twice and uninstall are **not run**.

| Separate spike | Result | Numbers / limits |
| --- | --- | --- |
| Phase0-W120 native export | pass | 120 pages, 108 headings, exact tree and target pages, 1.3188386 seconds |
| Text/fonts/images | pass | Minimum similarity 100%, 0 unembedded fonts, 0% page image coverage |
| A4/title | pass | 120 A4 portrait pages; source title preserved |
| Event cancellation | pass | 2/2 callbacks; 0 output PDFs |
| Event supplies range/copies | fail for event-only design | Only Doc/Cancel; choices absent from event parameters |
| IPP queue creation | blocked | 0x80070005, non-elevated token; payload checks not run |
| CAD API | pass, metadata only | FileName/PlotToFile in 2024/2027; runtime checks not run |

Phase0-W120 is not W1: TOC, images, spanning tables, hyperlink and mixed orientation are absent. DOCX SHA-256: `0ab97ec30f30ad108353d8ed67b5afb69d763dc87b151bd00cc20ef52599bd85`. PDFs and generated results: Tests/bin/virtual-printer/phase0. Retained evidence: Tests/VirtualPrinter/Evidence/2026-10-07.

Open gates in workflow order:

1. Preserve Word selected print settings; heading export itself passes.
2. Measure vector IPP transport before choosing a converter.
3. Prove normal CAD Ctrl+P path and finished-file capture.
4. Implement/measure shelf delivery, auto-start, isolation and recovery later.
5. Run Win10, 32-bit Office, full samples, heuristic and installer lifecycle tests later.
