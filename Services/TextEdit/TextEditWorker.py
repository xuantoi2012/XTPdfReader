# Real text editing for PDF Reader Pro. Run by Services/TextEdit/TextEditService.cs with the same embedded Python as the MuPDF worker:
#   python -u TextEditWorker.py job.json
# modes:  "area"  - the text runs of SEVERAL pages that lie inside a rectangle (fractions of each page), for batch find / replace
#         "pick" / "pickArea" / "delete" - drawn objects (lines, shapes, images) of a page: find them at a point or inside a rectangle, remove them
#         "runs"  - the text runs (same font, size and colour on one line) of one page, in points of the page as it is displayed (origin top left)
#         "apply" - REMOVES the old characters of the runs from the page (redaction of that rectangle only; lines, images and other text stay).
#                   The new text is written afterwards by TextEditWriter.cs (iText: an embedded Arial subset with a correct ToUnicode).
# One JSON object per output line.
import json
import math
import sys

import pymupdf as fitz


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


def job_area(job):
    """Runs whose centre lies inside the rectangle (fractions 0..1 of the displayed page) on each asked page. A page without any text says so."""
    doc = fitz.open(job["path"])
    if doc.needs_pass:
        doc.authenticate(job.get("password") or "")
    u1, v1, u2, v2 = job["rect"]
    for number in job["pages"]:
        page = doc[number - 1]
        w, h = page.rect.width, page.rect.height
        everything = runs_of(page)
        inside = []
        for run in everything:
            cx, cy = (run["bbox"][0] + run["bbox"][2]) / 2, (run["bbox"][1] + run["bbox"][3]) / 2
            if u1 * w <= cx <= u2 * w and v1 * h <= cy <= v2 * h:
                run["bg"] = background(page, fitz.Rect(run["bbox"]))
                inside.append(run)
        emit(type="area", page=number, width=w, height=h, rotation=page.rotation, hasText=len(everything) > 0, runs=inside)


# -- drawn objects ----------------------------------------------------------

def objects_of(page):
    """Lines, shapes (any other vector path) and images of a page, with the box of each (points of the displayed page, origin top left)."""
    result = []
    for index, d in enumerate(page.get_drawings()):
        items = d["items"]
        if not items:
            continue
        is_line = d.get("fill") is None and all(i[0] == "l" for i in items)
        r = d["rect"]
        result.append({"kind": "line" if is_line else "shape", "index": index, "bbox": [r.x0, r.y0, r.x1, r.y1], "width": d.get("width") or 0,
                       "filled": d.get("fill") is not None,
                       "segments": [[i[1].x, i[1].y, i[2].x, i[2].y] for i in items if i[0] == "l"] if is_line else []})
    for k, info in enumerate(page.get_image_info(xrefs=True)):
        r = fitz.Rect(info["bbox"])
        if not r.is_empty:
            result.append({"kind": "image", "index": k, "bbox": [r.x0, r.y0, r.x1, r.y1], "width": 0, "filled": True, "segments": [], "xref": info.get("xref", 0)})
    return result


def distance_to_segment(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    length2 = dx * dx + dy * dy
    t = 0 if length2 == 0 else max(0, min(1, ((px - ax) * dx + (py - ay) * dy) / length2))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def strip(obj):
    return {k: obj[k] for k in ("kind", "index", "bbox", "xref") if k in obj}


def job_pick(job):
    doc = fitz.open(job["path"])
    page = doc[job["page"] - 1]
    x, y, tol = job["x"], job["y"], job.get("tol", 2.5)
    hits = []
    for obj in objects_of(page):
        x0, y0, x1, y1 = obj["bbox"]
        reach = tol + obj["width"] / 2
        if obj["kind"] == "line":
            if not any(distance_to_segment(x, y, *seg) <= reach for seg in obj["segments"]):
                continue
        elif obj["kind"] == "shape":
            if not (x0 - reach <= x <= x1 + reach and y0 - reach <= y <= y1 + reach):
                continue
            # a big outline (a frame, a border) is hit on its edge, not in the middle
            inside = x0 + reach < x < x1 - reach and y0 + reach < y < y1 - reach
            if inside and (x1 - x0) > 40 and (y1 - y0) > 40 and not obj["filled"]:
                continue
        else:
            if not (x0 <= x <= x1 and y0 <= y <= y1):
                continue
        hits.append(obj)
    # smallest first: a line over a picture, a small shape inside a big one
    hits.sort(key=lambda o: (o["kind"] == "image", (o["bbox"][2] - o["bbox"][0]) * (o["bbox"][3] - o["bbox"][1])))
    emit(type="pick", page=job["page"], width=page.rect.width, height=page.rect.height, rotation=page.rotation, objects=[strip(o) for o in hits[:8]])


def job_pick_area(job):
    doc = fitz.open(job["path"])
    page = doc[job["page"] - 1]
    x0, y0, x1, y1 = job["rect"]
    box = fitz.Rect(x0, y0, x1, y1)
    inside = [o for o in objects_of(page) if box.contains(fitz.Rect(o["bbox"]))]
    emit(type="pick", page=job["page"], width=page.rect.width, height=page.rect.height, rotation=page.rotation, objects=[strip(o) for o in inside[:5000]])


def redraw(page, d):
    shape = page.new_shape()
    for item in d["items"]:
        if item[0] == "l":
            shape.draw_line(item[1], item[2])
        elif item[0] == "c":
            shape.draw_bezier(item[1], item[2], item[3], item[4])
        elif item[0] == "re":
            shape.draw_rect(item[1])
        elif item[0] == "qu":
            shape.draw_quad(item[1])
    shape.finish(fill=d.get("fill"), color=d.get("color"), width=d.get("width") or 1, dashes=d.get("dashes"), even_odd=d.get("even_odd", False),
                 closePath=d.get("closePath", False), lineCap=(d.get("lineCap") or [0])[0], lineJoin=d.get("lineJoin", 0),
                 fill_opacity=d.get("fill_opacity") or 1, stroke_opacity=d.get("stroke_opacity") or 1)
    shape.commit()


def job_delete(job):
    """Removes objects. Vector paths go with the redaction of their box (a path is removed when the box covers it), so paths that only
    happen to lie inside the box are put back afterwards (a deleted frame must not take what it frames)."""
    doc = fitz.open(job["path"])
    if doc.needs_pass:
        emit(type="error", message="The file is protected by a password; this edit needs an unprotected file.")
        return
    by_page = {}
    for target in job["objects"]:
        by_page.setdefault(target["page"], []).append(target)
    removed = 0
    for number, targets in sorted(by_page.items()):
        page = doc[number - 1]
        drawings = page.get_drawings()
        wanted = {t["index"] for t in targets if t["kind"] != "image"}
        images = [t for t in targets if t["kind"] == "image"]
        spared = []
        for index in wanted:
            if index >= len(drawings):
                continue
            d = drawings[index]
            reach = max(0.5, (d.get("width") or 0) / 2 + 0.3)
            box = fitz.Rect(d["rect"]) + (-reach, -reach, reach, reach)
            page.add_redact_annot(box, fill=False)
            for k, other in enumerate(drawings):
                if k not in wanted and k not in spared and box.contains(fitz.Rect(other["rect"])):
                    spared.append(k)
            removed += 1
        for image in images:
            page.add_redact_annot(fitz.Rect(image["bbox"]), fill=False)
            removed += 1
        page.apply_redactions(images=fitz.PDF_REDACT_IMAGE_REMOVE if images else fitz.PDF_REDACT_IMAGE_NONE,
                              graphics=fitz.PDF_REDACT_LINE_ART_REMOVE_IF_COVERED if wanted else fitz.PDF_REDACT_LINE_ART_NONE,
                              text=fitz.PDF_REDACT_TEXT_NONE)
        for k in spared:
            redraw(page, drawings[k])
    doc.saveIncr()
    emit(type="done", removed=removed)


def main():
    with open(sys.argv[1], encoding="utf-8") as handle:
        job = json.load(handle)
    try:
        {"runs": job_runs, "apply": job_apply, "area": job_area, "pick": job_pick, "pickArea": job_pick_area, "delete": job_delete}[job["mode"]](job)
    except Exception as ex:  # report, the caller shows it
        import traceback
        sys.stderr.write(traceback.format_exc())
        emit(type="error", message=str(ex))


if __name__ == "__main__":
    main()
