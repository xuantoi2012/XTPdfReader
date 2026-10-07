# Virtual printer phase 0 - 2026-10-07

**Decision: HOLD. Do not implement the IPP service or product installer yet.**

Word heading export works. The proposed DocumentBeforePrint-only interception does not supply the selected range/copy settings. Silently exporting the entire document would violate the acceptance criteria. IPP payload formats and CAD Ctrl+P output-path discovery remain unverified. The report distinguishes measurements, source/API inspection, and `not run` work.

## Environment and evidence

Windows 11 Pro 25H2 **26200.9550**, x64, non-elevated token. Registry ProductName says Windows 10 Pro, but the build identifies Windows 11; this is not a Windows 10 test. No Windows 10 machine was available.

| Software | Installed version |
| --- | --- |
| Word, Excel, PowerPoint | 16.0.20430.20092; Word COM build 16.0.20430 |
| Chrome | 154.0.8037.98 |
| Edge | 154.0.4258.53 |
| AutoCAD 2024 | R24.3.236.0.0 |
| AutoCAD 2027 | R26.0.118.0.0 |
| Microsoft IPP Class Driver | Installed |

Commands: Tests/VirtualPrinter/README.md. Retained evidence: Tests/VirtualPrinter/Evidence/2026-10-07/*.json. Generated documents/PDFs: Tests/bin/virtual-printer/phase0 (ignored).

Actual XTToolbox path is C:\Users\Admin\source\repos\XTToolbox, not the prompt's relative ..\XTToolbox. Existing changes in both repos were preserved. No production code was changed by this spike.

## 0a. IPP formats

A disposable loopback Python capture endpoint was started on 127.0.0.1:18631, advertising PDF, XPS and OXPS. It is not a production service or a validated IPP server.

Exact attempted command:

```powershell
Add-Printer -Name 'XT Phase0 Probe' -IppURL 'http://127.0.0.1:18631/ipp/print' -ErrorAction Stop
```

Result: `HRESULT 0x80070005,Add-Printer`, "Access was denied to the specified resource." Token elevation check returned false. No queue was created, no IPP request/document was received, and the endpoint was stopped. This is an environment block, not proof that IPP cannot preserve vectors.

| App | Windows 11 payload / searchable text | Windows 10 payload / searchable text |
| --- | --- | --- |
| Notepad | not run - queue creation denied | not run - no host |
| Word | not run - queue creation denied | not run - no host |
| Excel | not run - queue creation denied | not run - no host |
| Chrome | not run - queue creation denied | not run - no host |
| Edge | not run - queue creation denied | not run - no host |
| PowerPoint | not run - queue creation denied | not run - no host |
| PDF viewer | not run - queue creation denied | not run - no host |

An elevated session and a Win10 host are needed. Capture streams with vector-only capabilities and separately with PWG raster advertised. Failures against this minimal capture endpoint must be checked against a conforming endpoint before rejecting IPP. No converter has been selected.

## 0b. Word heading export and event

### Export measured

A generated structural DOCX has 120 explicit A4 pages, 12 Heading 1, 48 Heading 2 and 48 Heading 3 paragraphs, Vietnamese text, page markers and three small tables. It is **not full W1**: no TOC, ten images, spanning tables, hyperlink or landscape section.

DOCX SHA-256: `0ab97ec30f30ad108353d8ed67b5afb69d763dc87b151bd00cc20ef52599bd85`.

Export: ExportAsFixedFormat(PDF, OptimizeForPrint, CreateBookmarks=Heading, DocStructureTags=true, IncludeDocProps=true).

| Check | Result | Measurement |
| --- | --- | --- |
| Export | pass | 120 pages; export call 1.3188386 seconds |
| Heading tree | pass | 108/108 ordered titles, levels and pages match exactly; max page error 0 |
| Text | pass | Minimum normalized per-page similarity 100%; Vietnamese diacritics preserved |
| Embedded fonts | pass | 0 unembedded fonts |
| Page-image coverage | pass | 0% maximum |
| Geometry | pass | All 120 pages A4 portrait within 1 point |
| PDF title | pass | XT Phase 0 Word 120 Pages |
| Author | observation | Source author XT Virtual Printer Spike is preserved; not Windows username |
| Shelf/service end-to-end behavior | not run | No XT queue/service/add-in |

PyMuPDF checked text, font embedding and geometry on all pages. Pages 1, 60 and 120 were rendered and visually inspected without reading or controlling any app window. Visual review is representative, not all 120 pages.

### Event measured

The .NET 10 console probe subscribes to the installed Word PIA event, opens the generated DOCX read-only in its own hidden Word instance and cancels two PrintOut requests. Microsoft Print to PDF was used because no XT Reader queue exists. The proposed printer-name filter and Ctrl+P Backstage interaction were not tested.

| Caller request | Callback parameters | ActivePrinter | Selection | Cancellation |
| --- | --- | --- | --- | --- |
| Pages=5-9, Copies=2 | Doc, Cancel | Microsoft Print to PDF | Start=0, End=0 | No output PDF |
| Pages=10-11, Copies=1 | Doc, Cancel | Microsoft Print to PDF | Start=0, End=0 | No output PDF |

Two callbacks fired; Cancel=true prevented both outputs. JSON `request` labels are injected by the probe, not passed by Word. Reflection and [Microsoft's event contract](https://learn.microsoft.com/en-us/office/vba/api/word.application.documentbeforeprint) confirm only Doc and Cancel. ActivePrinter is readable, but Pages/Copies/Collate/grayscale/scaling are not event parameters.

**Design gap:** event-only interception can cancel printing and export headings, but cannot reconstruct selected print options from its parameters. This does not prove every other Word mechanism fails. Dialog state or alternate hooks were not tested. Whole-document export or an extra range prompt requires an owner decision and does not silently satisfy current range/copies tests.

32-bit Office, in-process COM add-in registration and .NET 10 deployment without VSTO are **not run**. A working external event sink is not proof of in-process add-in compatibility.

## 0c. CAD plot path

Installed assembly metadata was inspected without loading AutoCAD. Both versions expose PlotReactorManager.BeginDocument and BeginDocumentEventArgs.FileName, PlotToFile, Copies, DocumentName and PlotInfo. Constructor parameters include fileName. These are promising APIs, not measured runtime values.

| Check | AutoCAD 2024 | AutoCAD 2027 |
| --- | --- | --- |
| FileName / PlotToFile API exists | pass - metadata only | pass - metadata only |
| Actual PDF path during Ctrl+P | not run | not run |
| File closed at completion; cancellation | not run | not run |
| Watcher fallback / duplicate prevention | not run | not run |
| PC3 clone quality vs DWG To PDF | not run | not run |

DWG To PDF.pc3 exists in both installations; no PC3 was modified or deployed. No owner-supplied three-layout DWG was available. AutoCAD 2027 was running at the final environment check and was left untouched. XTToolbox's CLAUDE.md reserves the runner that lowers SECURELOAD for the user; it was not run. No security setting was changed. A non-UI runtime plot-reactor probe remains necessary.

Source review: XTBatchPlotService already knows outPdf in BeginDocument(..., true, outPdf). This proves XT_PRINT's existing path, not normal Ctrl+P. XTPdfReaderIncomingBridge writes completed paths to the existing pipe but returns false when Reader is absent; auto-start remains future work. Bridge/XT_PRINT/.xtset code was not changed.

## Gate before phase 1

1. Choose and prove preservation of Word range/copies/other print settings, or explicitly revise those requirements with the owner.
2. Capture actual IPP payloads per app in an elevated session on Win11 and Win10.
3. Measure CAD Ctrl+P output path, completion and cancellation in both AutoCAD versions, then compare the requested three layouts against DWG To PDF.

No service, production add-in, converter, installer change or heuristic bookmark feature was implemented. No restrictive dependency was added. The phase-0 report gate remains closed.
