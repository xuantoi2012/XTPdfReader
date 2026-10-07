# Prompt: build the XT Reader virtual printers (give this whole file to the coding agent)

You are working in two Windows/.NET repos that sit side by side:

- `XTPdfReader` (this repo, WPF, .NET 10; the PDF reader "PDF Reader Pro"; read `docs/ROADMAP.md`, `docs/TODO-2026-10-07.md`, `Tests/GpuPdfium/INCOMING-MERGE-SHELF-2026-10-06.md`).
- `XTToolbox` (AutoCAD plug-in suite, sibling folder `..\XTToolbox`; read its `CLAUDE.md`, `docs/XT_ARCHITECTURE.md` and the newest `XT_HANDOFF_*.md`). Other people have uncommitted edits in `XTToolbox`: **only touch the files you need and never commit anything you did not write.**

Answer and write chat messages in Vietnamese; code, comments and docs in English.

## Goal

Replace the owner's current routine (print to **pdfFactory Pro** to collect jobs, then open them in **Foxit** to merge) with this: every print job from any Windows app, and every AutoCAD plot, ends up as a PDF in the Reader's **Merge shelf** (the temporary shelf in the Merge window). The Reader then views, orders and merges. The owner only used pdfFactory as a holding area, so no pdfFactory-specific features are needed.

Hard requirements from the owner:

1. **Ctrl+P must keep working in every app, including AutoCAD.** No special "plot" command for users.
2. **AutoCAD quality must equal `DWG To PDF.pc3`** (vector, line weights, CTB, SHX text, layers). Therefore the AutoCAD engine does the plotting; we only collect its output.
3. **Word, Excel, browsers:** text and lines must stay **vector and sharp** (searchable text, embedded fonts, no raster page images). **Word documents (often 100+ pages) must get bookmarks from their Heading styles.** Excel needs no bookmarks.
4. Machines: Windows 10 and 11, the XTToolbox installer runs once with **admin rights**. Internal company use only (no distribution), so AGPL components such as Ghostscript/MuPDF are acceptable for now but must be isolated behind an interface and documented as a licence risk.
5. **Do not use the Windows built-in OCR** and do not assume any commercial SDK. No network services; everything stays on the machine.
6. Never drive the desktop (mouse/keyboard automation) without telling the owner first; build and test headless where possible. The repos' test runners are described in `docs/TODO-2026-10-07.md` section 5.

## Existing pieces you must reuse

- Hand-off protocol: named pipe `XTPdfMergeApp_IncomingPdfPipe`; a producer writes **one absolute PDF path per line** after the file is complete; the running Reader puts it on the Merge shelf (it does not open a tab). A producer must not delete the file; the Reader owns it afterwards. C# client example: `XTToolbox/XTDrawing/Services/XTPdfReaderIncomingBridge.cs`.
- If the Reader is not running, the producer must start it (installed path discovery is part of the work) and retry the pipe.
- Per-page sheet info (`/XTSheet`, see `XTToolbox/docs/XT_PDF_SHEET_INFO.md`) and `.xtset` recipes already exist for XT_PRINT/XT_SHEETS; do not change them.
- AutoCAD multi-sheet printing is already done by `XTToolbox/XTDrawing/Services/XTBatchPlotService.cs` (XT_PRINT / XT_SHEETS) and already forwards finished PDFs to the shelf.

## Design decided so far (change it only with a written reason)

### A. General apps: printer "XT Reader" (Windows printer)
- A local **IPP** printer served by a small background Windows service (starts at logon/boot, listens on 127.0.0.1 only, URL reserved by the installer so the service can run as a normal user or LocalService). The printer is created once by the installer (`Add-Printer` with the "Microsoft IPP Class Driver", or `Add-PrinterPort`/`Add-Printer -ConnectionName`; find what works on **both** Win10 and Win11).
- Each print job arrives as its own stream with its own `job-name`/document title and `requesting-user-name`. The service stores it, converts it to PDF if needed, names it from the document title, and sends the path through the pipe (starting the Reader if necessary).
- **Phase 0 spike first** (see below): the format Windows sends decides the converter. Acceptable: PDF as is; XPS/OXPS converted to **vector** PDF. **Not acceptable:** PWG-raster/PCLm raster pages. If only raster can be had, fall back to the "Microsoft Print To PDF" driver + local file port + watcher, and tell the owner what is lost (document name, concurrent jobs).
- Fallback order if IPP fails: PostScript driver with a pipe port (the way PDF24 works; the machine already has PDF24) + PostScript-to-PDF converter; own V4 driver only as a last resort.

### B. Word headings -> bookmarks
- A small **Word COM add-in** installed with XTToolbox. It handles `Application.DocumentBeforePrint`: when `ActivePrinter` is "XT Reader", cancel the normal print and call `Document.ExportAsFixedFormat` with `ExportFormat = wdExportFormatPDF`, `CreateBookmarks = wdExportCreateHeadingBookmarks`, `DocStructureTags = true`, `IncludeDocProps = true`, `OptimizeFor = wdExportOptimizeForPrint`, to a temp file named from the document title, then send the path through the pipe. The user still just presses Ctrl+P and picks "XT Reader".
- Open question for the spike: does `DocumentBeforePrint` give the page range / copies the user chose? If not, decide with the owner (default: whole document, plus a tiny range prompt only when the user has a selection or a range).
- Must work for 32- and 64-bit Office and Microsoft 365; use per-machine registration done by the installer; no VSTO runtime dependency if a COM-visible .NET add-in can do it (check what .NET 8+/10 supports; document the choice).
- **Fallback when the add-in is not active** (other apps, Word without add-in): the Reader derives bookmarks from the PDF itself: text lines clearly larger and/or bolder than the body text become bookmarks, levels from the size ranking. Implement this as a Reader feature (`Services/AutoBookmarks`, "Add bookmarks from headings" in the Bookmarks tab, optional automatic step for jobs from the printer), tested on generated PDFs. It is a heuristic: show a preview and let the user accept.

### C. AutoCAD with Ctrl+P
- Ship **`XT Reader (CAD).pc3`**, a copy of `DWG To PDF.pc3` (same engine and settings; custom paper sizes already used by the company if the owner provides them). It appears in the normal Plot dialog printer list. Name the Windows printer "XT Reader (Office)" or similar so users do not confuse the two.
- **Phase 0 spike inside AutoCAD (2024 and the 2027 build the owner uses):** can the plug-in learn the output PDF path of a plot done with that PC3 (`PlotReactorManager` events, `Document` events, command events)? If yes, send it to the shelf automatically. If not, set the default plot-to-file folder to a Reader inbox folder, let the user press Enter on the file-name dialog, and have the Reader (or the XTToolbox plug-in) watch that folder (`FileSystemWatcher`, wait until the file is closed and starts with `%PDF`) and forward new files. Report which one works; do not use UI-automation hacks on the dialog unless the owner agrees.

## Phases and acceptance

0. **Spikes (write results to `docs/VIRTUAL-PRINTER-SPIKES.md`, with the exact commands and a table):**
   a. Which document format reaches an IPP printer from Notepad, Word, Excel, Chrome, Edge, PowerPoint and a PDF viewer, on Win10 and Win11. Is text still text?
   b. Word `DocumentBeforePrint` information available; export with heading bookmarks of a generated 120-page document.
   c. AutoCAD plot-output discovery as above.
   Do not continue to phase 1 before the owner has read the spike report if a spike shows the design cannot work.
1. IPP service + printer installer script (idempotent, uninstall included), pipe delivery, Reader auto-start, clean logs. Unit tests for the IPP request/response handling and the job-to-PDF conversion with sample jobs.
2. Word add-in and installer registration.
3. Reader: heading-based auto bookmarks (fallback) with tests.
4. AutoCAD PC3 + output capture.
5. Installer integration in XTToolbox (find how it installs today; ask if unclear) and an uninstall that removes the printers, service, add-in and PC3.

Each phase ends with: tests green, a short note appended to the newest `XT_HANDOFF_*.md` (or a new one for the day), a commit containing only your files. Use `docs/PROMPT-VIRTUAL-PRINTER-TESTS.md` for the test matrix and acceptance numbers.

## Rules

- Follow the existing code style; keep Windows-service, add-in, converter and Reader code in separate projects/folders with clear interfaces.
- No secrets in the repo; the service must reject non-loopback connections and ignore requests not from a local user.
- Ask the owner (in Vietnamese) before: adding a NuGet/native dependency with a restrictive licence, changing the installer's behaviour, or anything that needs the desktop to be driven.
- If something here cannot be done as written, stop and explain with evidence rather than silently doing something else.
