# PDF Reader Pro — roadmap (agreed with the owner, 2026-10-05)

Goal: beat Foxit for engineers and design teams, not by copying every feature but by what Foxit does badly: drawing-set merging, layers, team saving,
sheet awareness. Rendering speed is already on par. Out of scope on purpose (low demand, complex): forms, digital signatures, redaction.

Companion docs in XTToolbox: `docs/XT_PDF_SHEET_INFO.md` (per-page sheet data), `docs/XT_SET_FILE.md` (`.xtset` recipe). Status of the day-to-day work: `XT_HANDOFF_*.md`.

Check how things behave without opening the app: `dotnet build Tests/PerformanceTests.csproj -c Release -o <dir>`, then
`dotnet <dir>\XTPdfMergeApp.PerformanceTests.dll --ui-smoke` (real windows off-screen, PNGs in `<dir>\results\ui-*.png`) or `--layer-merge-only` (engine tests). Full suite: no argument.

## Done (built and tested; UI checked by rendering, not yet by hand in the real app)

- Layers when merging: 3 modes (separate per file as a tree / merge by name / keep chosen + prefix), checklist, "Rename result layers…". Same table ported to XTToolbox (`XTLayerMergeWindow`).
- Manage layers dialog (rename, merge, strip `Xref|`, save to file or copy).
- Tab strip: equal widths, `+` next to the last tab, list button only on overflow; tab menu (location, copy file, copy path, close all, replace sheets).
- Drag a tab onto another tab → merge draft (one Undo step). Replace sheets from a revision (match by sheet no, else DWG + layout; one Undo step).
- Sheet info in PDF (`/XTSheet`, `/XTProject`) written by XT_PRINT/XT_SHEETS; Sheets tab (list, filter, Open DWG, Split by group/subset/DWG).
- `.xtset` + `.xtparts` written by XT_SHEETS/XT_PRINT; Reader opens it and Rebuilds (layers + bookmarks from sheet info).
- Sequential saving: in-place edits retry on top of another user's save; History tab (who/when/what, recover the version before a save); "last saved by" in the disk-changed banner.

## Agreed, to do

### A. Print: page sizes vs the printer (Reader Print window) — all DONE (2026-10-05): summary, printer match, warning, size badge on thumbnails + "Select pages of this size", print each size on its own printer/paper
Today one paper size is chosen for the whole job; a file with A0/A1/A3/Letter pages fails or scales wrongly at the printer.
1. Page-size summary at the top of the Print window (`A1 × 12 · A3 × 40 · Letter × 1`); click a row = select those pages.
2. Match every size against the printer's paper list: exact / nearest paper that fits / nothing fits (say which pages).
3. Warn before printing, with "Copy list". 4. Show the size on Pages/Sheets thumbnails + filter "pages of this size".
5. Later: "choose paper per page size" (split the job by size, one paper per group; for pages that do not fit: skip / shrink to the biggest paper / other printer).
Reuse: `PdfExportService.SizeName`, `PrintWindow` paper list, `PdfThumbnailService.GetPageSizesAsync`.
XT_PRINT side: the "cannot determine the paper size" stop (`BatchPlotService.ApplyAutoPaper`) should list which sheets and which sizes the printer lacks.

### B. Read sheet number / title from a PDF that has no `/XTSheet` — B.1 and B.2 DONE (2026-10-05): Sheets tab > "Read info…"; B.3 (OCR) open
1. PDFs with a text layer (CAD exports): the user marks the title-block area once per paper size, the app reads the text there (number, title, scale) and fills `/XTSheet`. No OCR needed.
2. A review table before writing (number | title | page | confidence; low confidence highlighted). Mandatory: one wrong character breaks revision matching.
3. Scans: OCR on the marked area only. Engine behind one interface (see "OCR engine" below).
4. Once `/XTSheet` exists, Sheets tab, find-by-number, bookmarks, replace-by-revision, split and `.xtset` all work on third-party PDFs.

### C. Team saving — DONE (2026-10-05) except what is listed below
- "Someone is editing" presence (small lock file + name in the tab). Conflict dialog when two people changed the same annotation (today: the later save is applied on top).
- Page edits saved through `PdfFileTransaction` (delete/reorder) still refuse when the file changed: replay or ask.

### D. Housekeeping
- Layer toggle "no visible change" bug: not reproduced (renders correctly in tests). Needs the owner's file and whether the checkbox is dimmed (layers controlled by `/D/AS` usage are deliberately not toggleable).
- `XTPdfMerger` exists twice (XTDrawing and Reader). Owner decision: XT_SHEETS keeps merging, so keep both and keep `LayerMergePolicy` in sync (`XTLayerMergePolicy.cs` ↔ `Services/LayerMergePolicy.cs`; also `XTSheetPdfInfo.cs`).
- Look at the XTToolbox layer dialog inside AutoCAD once (needs `Application.ShowModalWindow`).
- XT_SHEETS: bookmarks are only added when a cover or TOC is included (`XTSheetSetManagerWindow` ~3612). Confirm that is intended.
- Nothing is committed yet in either repo (as of this writing).

## OCR engine options (for B.3)

| Option | Notes |
|---|---|
| **ABBYY FineReader PDF 16** (installed on the owner's machine, has Vietnamese; `FineReader.exe`, `HotFolder.exe`, `finereaderocr.exe`) | Best accuracy, but no public API and no `FineCmd.exe` found. Usable through **Hot Folder**: a task watches a folder, recognises, writes a searchable PDF (text layer) that B.1 then reads. Fine for the owner's own batch use; **a product shipped to other users cannot assume they own FineReader**, and automating a desktop licence for others is not what the licence is for. The redistributable ABBYY option is the paid *FineReader Engine* SDK. |
| Windows built-in OCR (`Windows.Media.Ocr`) | Free, no install if the Vietnamese language pack is present; lower accuracy on small title-block text. |
| Tesseract | Free, redistributable, Vietnamese models available; needs preprocessing for scans. |

**Vietnamese OCR candidates (owner: prefer Vietnamese-made projects, not the Windows OCR):** `pbcquoc/vietocr` (Apache 2.0, transformer text-recognition model for Vietnamese print and handwriting; recognition only, needs a text detector such as DBNet/PaddleOCR in front),
`viethung21IT/OCR_Studio` (shows an ONNX DBNet + VietOCR pipeline), `vitmetmoi/Vietnamese-OCR` (PaddleOCR fine-tuned for Vietnamese detection and recognition), `nguyenq/VietOCR3` / `ADTC/VietOCR` (Java GUI around Tesseract, not a library for us).
Plan: benchmark on 20 real title blocks (Tesseract vs PaddleOCR vs VietOCR-ONNX) before choosing; check each model's licence and weights before shipping them.

Design: an `IOcrProvider` (image/region in → text + confidence out). First implementation = whichever is quickest to try; ABBYY Hot Folder as an optional provider for machines that have it.
A spike worth doing before committing: create one Hot Folder task (PDF → searchable PDF, Vietnamese), drop a scanned drawing, and measure time and how well B.1 reads the title block from the result.

## Ideas — owner agreed to all of them. Status: 1 measure, 2 compare, 3 register, 4 sheet links, 5 page labels, 6 review summary, 7 print by size, 8 folder search are DONE; 9 left out on purpose. OCR (B.3) goes last.

1. **Measure with the sheet scale**: `/XTSheet` already carries `1:100`, so measuring distance/area on a sheet can give real-world units without calibrating. Foxit needs manual calibration per page.
2. **Compare two revisions** of a sheet (overlay or side by side, differences tinted), matched through `/XTSheet`. Pairs with "Replace sheets from revision".
3. **Drawing register export**: sheets list (number, title, scale, group, size, revision) to Excel/CSV straight from `/XTSheet`.
4. **Clickable TOC and cross-references**: link TOC lines and "see KT-05" text on sheets to the sheet pages automatically.
5. **Real page labels**: write `/PageLabels` from sheet numbers so every viewer shows `KT-05` instead of page 37.
6. **Review summary per sheet**: open / resolved comment counts per sheet in the Sheets tab; comment export grouped by sheet.
7. **Route printing by size**: A1/A0 pages to the plotter, A3/A4 to the laser printer in one action.
8. **Search across a folder or all open files** (sheet number or text), with results grouped by file.
9. Auto-reload when a file changes on disk and the user has no unsaved changes.

## Notes on the items built after the first roadmap
- **Measure** (Tools > Measure): drag between two points; label shows the real length at the page's scale (`/XTSheet /Scale`, else asked once per page, kept for the session). Not written to the PDF. Next steps if wanted: area / polyline, save as an annotation.
- **Compare versions** (tab menu): matches sheets by number (else page by page if page counts are equal), overlay red = only old, blue = only new, list shows % changed. Tolerates 1 px shift.
- **Page labels** (Sheets tab > Page labels): writes sheet numbers as `/PageLabels`; pages without a number keep their page number.
- Idea 9 (auto reload) left out on purpose: reloading closes and reopens the tab, which changes the tab order and jumps the view while the user reads. The banner stays.
- **Sheet links** (Sheets tab > Link numbers): finds sheet numbers (from sheet info, at least 3 characters, unique) in the text of other pages (contents lines, "see KT-05") and adds GoTo links; skips a sheet's own page and places that already have a link. Needs a real text layer.
- **Review summary**: Sheets rows show "N open / N resolved" comments per sheet (read in the background); typing "open" in the filter box lists the sheets with unresolved comments.
- **Print by size** (Print window > Pages by size… > Print each size on its own printer / paper…): one printer per paper size, paper chosen per printer (exact, else next larger, else biggest with shrink %).
- **Folder search** (Find tab > Folder…; right-click it to change the folder): all PDFs in a folder and sub-folders (max 500); a hit in a file that is not open opens it.

## Team saving, what was added last (2026-10-05)
- **Presence** (Settings > "Show when someone else has the same file open", default off): each Reader writes a small hidden marker `.<file>.xtopen.<user@machine>.<pid>` next to the PDF (heartbeat 30 s, ignored after 100 s, deleted after 10 min, removed on close). A toast says who else has the current file open. `Services/XTPresence.cs`, `ReaderWindow.Presence.cs`.
- **Same comment edited by two people**: the later save replaces the text (as before) but is now detected and reported; the other person's version is recoverable from History ("Save the version before this change as…"). No blocking dialog on purpose (the save runs in the background).
- **Page edits** (delete / reorder) saved while the file changed on disk still refuse, now naming who saved last (from the file history) and asking to reload.
- XT_SHEETS prints without cover/TOC now get the Hạng mục > Subset > Sheet bookmarks too (`BatchPlotService.Execute`; compiled, not run inside AutoCAD).

## OCR (B.3) — still open, needs the owner's input
Not started on purpose: choosing the engine needs a benchmark on real scans (Tesseract vs PaddleOCR/ONNX vs VietOCR), and model files need their licences checked. Needed from the owner: ~20 real scanned title blocks (or whole sheets) and which of the candidates in the section above to try first.
