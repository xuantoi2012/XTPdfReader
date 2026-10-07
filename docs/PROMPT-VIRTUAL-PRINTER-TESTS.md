# Prompt: test the PDFs produced by the XT Reader virtual printers (give this whole file to the coding agent)

Context: read `docs/PROMPT-VIRTUAL-PRINTER.md` first (goal, design, phases). This file describes **what to test and how to judge it**. Write chat messages in Vietnamese; code, scripts and docs in English. Put everything under `Tests/VirtualPrinter/` in `XTPdfReader` and write the results to `docs/VIRTUAL-PRINTER-TEST-RESULTS-<date>.md` with a pass/fail table and the numbers.

Do not drive the owner's desktop (no mouse or keyboard automation, no screenshots of other windows) unless the owner says so for a named test. Printing through COM/command line (`Word.Application.PrintOut`, `Start-Process -Verb PrintTo`, `Out-Printer`) is allowed. Use only generated or owner-supplied sample files; never modify an original.

## 1. Sample documents (generate them with scripts; keep the generators in the repo)

| Id | Content | Why |
|---|---|---|
| W1 | Word, 120 pages: Heading 1 (12 chapters) > Heading 2 (4 each) > Heading 3 (some), body text, a table of contents, 10 images, 3 tables spanning pages, header/footer with page numbers, Vietnamese text with all diacritics, a hyperlink, one landscape section | headings -> bookmarks, vector text, mixed orientation |
| W2 | Word, 3 pages, **no** Heading styles, only bold 18 pt lines | the heuristic fallback must not invent a deep tree |
| W3 | Word, 300 pages (W1 repeated) | speed and memory |
| X1 | Excel, 4 sheets, wide tables, a chart, set to fit 1 page wide | vector lines/charts |
| P1 | PowerPoint, 12 slides with shapes, text, a picture | vector shapes |
| B1 | Web page printed from Chrome and from Edge (local HTML with Vietnamese text, an SVG, a table) | browser pipeline |
| N1 | Notepad text file, 40 lines | simplest case |
| M1 | Paint / an image viewer printing a photo | raster is expected here; it must not be downsampled below 300 dpi equivalent |

Generators: Word documents via COM or `python-docx`/OpenXML (python or PowerShell is fine; heading styles must be real built-in "Heading 1..3"). Record the generator output hashes.

## 2. How each sample is printed

For every sample, on **Windows 10 and Windows 11** (state which was used for each row) print to the printer "XT Reader" with default settings, and also with: landscape, a page range (pages 5-9), 2 copies, grayscale. Use COM (`Word.Application`, `Excel.Application`, `PowerPoint.Application` with `ActivePrinter`/`PrintOut`) and `Start-Process -Verb PrintTo` for the others. Print three jobs back to back without waiting, once with the Reader closed and once with it running.

## 3. What to check on every resulting PDF (automate with iText / PyMuPDF / the Reader's own `--` test hooks; no screenshots needed except where noted)

1. **Arrived**: the path appeared on the Merge shelf pipe within 10 s (30 s when the Reader had to start). Count of jobs in = count of shelf items out; no duplicates; no partially written file (the file opens and ends with `%%EOF`).
2. **Vector**: text is real text (extract the text of page 1 and compare it with the source: at least 99% of characters equal, Vietnamese diacritics intact); search finds a chosen sentence; fonts are embedded (list fonts, fail if a non-embedded non-standard font is used); page content contains **no full-page image** (largest image covers less than 50% of the page) for W1/W2/X1/P1/B1/N1; thin lines survive (render at 400% and check line pixels are crisp: no JPEG artefacts around them).
3. **Size and orientation**: page size matches the source (A4/Letter as set), landscape sections stay landscape, `/Rotate` consistent.
4. **Page range and copies**: pages 5-9 gives exactly 5 pages with the right text; 2 copies gives 2 documents or doubled pages according to the documented behaviour.
5. **Bookmarks** (W1 through the Word add-in): the outline tree equals the Heading structure exactly (same titles, same nesting, correct target pages, at most 1 page off for the heading page). Count equals the number of Heading 1-3 paragraphs. Hyperlinks and the document title/author are kept. For W2 the add-in path creates **no** bookmarks; the Reader fallback (Services/AutoBookmarks, when it exists) proposes only the 18 pt lines.
6. **Fallback heuristic** (any app, no add-in): on a PDF printed from W1 without the add-in, the proposed bookmarks match the Heading 1/2 titles with precision >= 90% and recall >= 80%; report both numbers and the missed/extra titles.
7. **Metadata**: PDF title = document title (not "Microsoft Word - ..." or a temp name), file name derived from it, sanitised, no collisions when two jobs have the same title (suffix), author = the Windows user.
8. **Performance** (W3, 300 pages): job complete and on the shelf within 60 s; the service's peak private memory < 500 MiB; the Reader stays responsive (report only; no desktop driving).
9. **Robustness**: cancel a job in the Windows print queue (no file, no shelf item, no stuck queue); kill the service in the middle of a job (queue recovers, next job works); Reader crash during delivery (file stays, delivered on next start); two users on one machine (separate sessions) do not see each other's jobs; the service refuses a connection from another machine (try from the LAN IP).
10. **Install and uninstall**: run the installer script twice (idempotent), print, uninstall (printers, port, service, add-in, PC3 and URL reservation all gone; nothing left in `C:\Windows\System32\spool\drivers` that was ours).

## 4. AutoCAD (only when the owner provides a DWG with 3 layouts: A1 landscape, A3 portrait, Letter)

Compare the PDF from the **same layout** plotted with (a) `DWG To PDF.pc3` selected by hand and (b) `XT Reader (CAD).pc3`: the two files must have the same page size, the same vector content (extracted path/text counts equal), the same layers (OCG names), line weights equal on a sampled set of 20 lines (compare stroke widths in the content stream), and equal rendering at 400% zoom (pixel difference below 0.5% of ink pixels). Report how the output path reached the shelf (event or watcher) and the delay.

## 5. Report format

For each sample x Windows version: a row with the checks above (pass/fail + number), the tool versions (Office build, Chrome/Edge version, AutoCAD version, Windows build), the generator hash, and an attached list of failures with the PDF names kept under `Tests/bin/virtual-printer/`. End with a short list of defects ordered by how much they hurt the owner's workflow (headings, vector quality and lost jobs first). Do not mark a check as passed when it was not run; say "not run" and why.
