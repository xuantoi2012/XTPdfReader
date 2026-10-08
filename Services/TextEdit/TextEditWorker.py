# Real text editing for PDF Reader Pro. Run by Services/TextEdit/TextEditService.cs with the same embedded Python as the MuPDF worker:
#   python -u TextEditWorker.py job.json
# modes:  "runs"  - the text runs (same font, size and colour on one line) of one page, in points of the page as it is displayed (origin top left)
#         "apply" - REMOVES the old characters of the runs from the page (redaction of that rectangle only; lines, images and other text stay).
#                   The new text is written afterwards by TextEditWriter.cs (iText: an embedded Arial subset with a correct ToUnicode).
# One JSON object per output line.
import json
import os
import sys

import pymupdf as fitz

WINDOWS_FONTS = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts")


def emit(**message):
    sys.stdout.write(json.dumps(message, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def runs_of(page):
    """Spans of a line that look the same and touch each other are one run (CAD exports often cut a word into pieces)."""
    result = []
    for block in page.get_text("dict")["blocks"]:
        for line in block.get("lines", []):
            current = None
            for span in line["spans"]:
                text = span["text"]
                if not text.strip():
                    if current is not None:
                        current["text"] += text
                        x0, y0, x1, y1 = span["bbox"]
                        current["bbox"][2] = max(current["bbox"][2], x1)
                    continue
                x0, y0, x1, y1 = span["bbox"]
                same = (current is not None and current["font"] == span["font"] and abs(current["size"] - span["size"]) < 0.05
                        and current["color"] == span["color"] and x0 - current["bbox"][2] < span["size"] * 0.6)
                if same:
                    current["text"] += text
                    current["bbox"] = [min(current["bbox"][0], x0), min(current["bbox"][1], y0), max(current["bbox"][2], x1), max(current["bbox"][3], y1)]
                else:
                    current = {"text": text, "bbox": [x0, y0, x1, y1], "origin": list(span["origin"]), "size": span["size"],
                               "color": span["color"], "font": span["font"], "flags": span["flags"]}
                    result.append(current)
    for run in result:
        run["text"] = run["text"].rstrip()
    return result


def background(page, rect):
    """Colour of the page just left of the run, so the preview can cover the old text with it (white when it cannot be read)."""
    try:
        probe = fitz.Rect(max(0, rect.x0 - 3), rect.y0, max(1, rect.x0 - 1), rect.y1) & page.rect
        if probe.is_empty:
            return 0xFFFFFF
        pix = page.get_pixmap(clip=probe, dpi=36, alpha=False)
        r, g, b = pix.pixel(0, 0)[:3]
        return (r << 16) | (g << 8) | b
    except Exception:
        return 0xFFFFFF


def job_runs(job):
    doc = fitz.open(job["path"])
    if doc.needs_pass:
        doc.authenticate(job.get("password") or "")
    page = doc[job["page"] - 1]
    runs = runs_of(page)
    for run in runs:
        run["bg"] = background(page, fitz.Rect(run["bbox"]))
    emit(type="runs", page=job["page"], width=page.rect.width, height=page.rect.height, rotation=page.rotation, runs=runs)


def font_file(flags):
    bold = bool(flags & 16)
    italic = bool(flags & 2)
    name = "arial" + ("bd" if bold else "") + ("i" if italic and not bold else "") + ("bi" if bold and italic else "")
    name = {"arial": "arial", "arialbd": "arialbd", "arialbi": "arialbi", "arialibi": "arialbi"}.get(name, name)
    path = os.path.join(WINDOWS_FONTS, name + ".ttf")
    return path if os.path.exists(path) else None


def rgb(color):
    return ((color >> 16 & 255) / 255.0, (color >> 8 & 255) / 255.0, (color & 255) / 255.0)


def job_apply(job):
    path = job["path"]
    doc = fitz.open(path)
    if doc.needs_pass:
        emit(type="error", message="The file is protected by a password; text editing needs an unprotected file.")
        return
    by_page = {}
    for edit in job["edits"]:
        by_page.setdefault(edit["page"], []).append(edit)
    applied = 0
    for number, edits in sorted(by_page.items()):
        page = doc[number - 1]
        # 1. take the old characters away (only text; the rectangle is a little smaller than the run so the lines around it stay)
        for edit in edits:
            x0, y0, x1, y1 = edit["bbox"]
            shrink = (y1 - y0) * 0.12
            page.add_redact_annot(fitz.Rect(x0 + 0.2, y0 + shrink, x1 - 0.2, y1 - shrink), fill=False)
        page.apply_redactions(images=fitz.PDF_REDACT_IMAGE_NONE, graphics=fitz.PDF_REDACT_LINE_ART_NONE)
        applied += len(edits)
    if job.get("inPlace", True):
        doc.saveIncr()
    else:
        doc.save(job["output"], garbage=0)
    emit(type="done", applied=applied)


def main():
    with open(sys.argv[1], encoding="utf-8") as handle:
        job = json.load(handle)
    try:
        {"runs": job_runs, "apply": job_apply}[job["mode"]](job)
    except Exception as ex:  # report, the caller shows it
        emit(type="error", message=str(ex))


if __name__ == "__main__":
    main()
