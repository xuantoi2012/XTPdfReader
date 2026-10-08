# Third-party components and their licences (review, 2026-10-08)

This is an engineering inventory for the owner's decision about releasing PDF Reader Pro and XT Capture outside the company. **It is not legal advice**: have a lawyer confirm
the points marked "decision".

## What ships

| Component | Where | Licence | Used for |
|---|---|---|---|
| **MuPDF** (inside PyMuPDF 1.28.2, `mupdf.dll` / `_mupdf.pyd`) | `MuPdfRuntime\Lib\site-packages\pymupdf` | **AGPL-3.0**, or a commercial licence from Artifex | the page rendering engine, text search / select, OCR |
| **PyMuPDF** 1.28.2 (python bindings) | same | **AGPL-3.0**, or Artifex commercial ("Dual Licensed") | talking to MuPDF from the worker scripts (`MuPdfWorker.py`, `OcrWorker.py`) |
| **iText Core** 9.6.0 (`itext`, `itext.bouncy-castle-adapter`) | Reader and XT Capture (PDF export) | **AGPL-3.0**, or iText commercial licence | editing PDFs: annotations, pages, merge, OCR text layer, export |
| **Tesseract** (compiled into MuPDF/PyMuPDF) + `vie.traineddata` (tessdata_fast) | `tessdata\vie.traineddata`, 0.5 MB | **Apache-2.0** | reading Vietnamese and Latin text of scans |
| **Python 3.12** (embedded runtime) | `MuPdfRuntime\` | PSF licence (permissive; keep `LICENSE.txt`) | runs the worker scripts |
| **PDFium** (`bblanchon.PDFium.Win32`) | only in the PDFium comparison / test build, not in the shipped MuPDF-only build | BSD-3-Clause (Apache-2.0 parts) | comparison tests |
| **Bouncy Castle** (via iText adapter) | with iText | MIT | encryption / signatures |
| **VirtualizingWrapPanel** 2.5.2 | Reader | MIT | thumbnail wrap panel |
| **XTStyle** | `..\XTStyle` | the owner's own | controls and themes |
| Icons (`UiIcons.xaml`, Phosphor-style paths) | Reader | MIT (Phosphor Icons) if they are Phosphor's; confirm the source file | ribbon icons |

Windows' own parts (WPF, .NET 10) are Microsoft's and need nothing from us beyond the .NET licence terms for redistribution of the runtime when the package is self-contained.

## The AGPL question (the one that matters)

MuPDF, PyMuPDF and iText are all AGPL-3.0 unless a commercial licence is bought. What AGPL asks, in short:

1. **Using the program inside one organisation, with no copy given to another party, asks for nothing.** Giving copies to employees of the same company is generally not "conveying" to a third party. The network clause (section 13) only applies if other people use the program through a network.
2. **Giving the program (or an installer) to anyone outside the company, or selling it, "conveys" it.** Then AGPL requires that the *whole program that links with these libraries* is offered under AGPL-3.0 with its **complete source code** to every recipient. PDF Reader Pro links with iText directly and runs MuPDF through its worker, so the Reader and XT Capture (it also uses iText) would be affected; the Python scripts in the package are AGPL work as well.
3. A closed-source release is only possible with **commercial licences**: Artifex (MuPDF + PyMuPDF) and iText. Prices are by quote and depend on how it is distributed.

Options before the program leaves the company (decision):

- **A. Keep it internal.** Nothing to buy or publish. Keep a record that it is for the company's own staff. Contractors working on company projects should be covered by their agreement; check.
- **B. Release as open source under AGPL-3.0.** Publish the source of the Reader, XT Capture, XTStyle (it is linked) and the worker scripts; keep the licence texts and notices; offer the source in the About box. The licence key / XTLicense idea would then protect nothing, since anyone can remove it.
- **C. Buy commercial licences** (Artifex for MuPDF / PyMuPDF, iText for iText Core) and keep the source private. Ask both vendors for the terms that match "desktop application, distributed to N users, through our own installer".
- **D. Replace the AGPL parts.** Rendering with PDFium (BSD) is possible: the PDFium path still exists in the code (`MuPdfOnly=false`), but the Reader moved to MuPDF for quality and speed. iText would need replacing (editing, annotations, merge, OCR layer, export): PdfSharp / PDFsharp (MIT) or QPDF-style tools cover part of it, a large job. OCR would need its own Tesseract build (Apache-2.0) instead of PyMuPDF's.

## Notices to keep and to add

- Ship a notices file (the lines of the table above with licence texts) in the installer and show it from About (a "Third-party notices" link). Not done yet.
- Tesseract data: the Apache-2.0 licence text and the note "tessdata_fast, https://github.com/tesseract-ocr/tessdata_fast" go with `vie.traineddata`.
- Python: keep `MuPdfRuntime\LICENSE.txt`.
- If AGPL is chosen (B), also add a source offer (a link) to About and to the installer page.

## Things to check later

- That the icon paths in `Resources/UiIcons.xaml` are Phosphor's (MIT) and the attribution is kept.
- The licence of any font embedded into PDFs: the OCR text layer embeds a subset of **Arial** (or Segoe UI / Tahoma) from the user's own Windows folder; subsets embedded for invisible text are normally acceptable, but Microsoft's font licence has a "no embedding in a way that lets the font be extracted" wording for some fonts, so a lawyer should confirm; an alternative is to embed an open font (for example Noto Sans, OFL) with the program.
- The `XTLicense` (Supabase) client libraries, when the licence window is built.
