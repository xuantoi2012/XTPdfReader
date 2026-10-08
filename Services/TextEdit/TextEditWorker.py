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
import os
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


def page_picture(page, dpi=30):
    """One small picture of the whole page, to read the colour of the paper beside the text from (None when it cannot be drawn)."""
    try:
        return page.get_pixmap(dpi=dpi, alpha=False)
    except Exception:
        return None


def background(picture, page, rect):
    """Colour of the page just left of the run, so the preview can cover the old text with it (white when it cannot be read)."""
    if picture is None:
        return 0xFFFFFF
    try:
        k = picture.width / max(1.0, page.rect.width)
        x = int(max(0, min(picture.width - 1, (rect.x0 - 2) * k)))
        y = int(max(0, min(picture.height - 1, (rect.y0 + rect.y1) / 2 * k)))
        r, g, b = picture.pixel(x, y)[:3]
        return (r << 16) | (g << 8) | b
    except Exception:
        return 0xFFFFFF


def job_runs(job):
    doc = fitz.open(job["path"])
    if doc.needs_pass:
        doc.authenticate(job.get("password") or "")
    page = doc[job["page"] - 1]
    runs = runs_of(page)
    picture = page_picture(page) if runs else None
    for run in runs:
        run["bg"] = background(picture, page, fitz.Rect(run["bbox"]))
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
        picture = None
        for run in everything:
            cx, cy = (run["bbox"][0] + run["bbox"][2]) / 2, (run["bbox"][1] + run["bbox"][3]) / 2
            if u1 * w <= cx <= u2 * w and v1 * h <= cy <= v2 * h:
                if picture is None:
                    picture = page_picture(page)
                run["bg"] = background(picture, page, fitz.Rect(run["bbox"]))
                inside.append(run)
        emit(type="area", page=number, width=w, height=h, rotation=page.rotation, hasText=len(everything) > 0, runs=inside)


# -- drawn objects ----------------------------------------------------------
# A page of a drawing holds tens of thousands of paths. Everything here works on page.get_cdrawings() (plain tuples, 2-3 times faster than get_drawings)
# and looks at the box of a path FIRST; only the few paths that pass get their kind, their points or their preview built. An object is named by its position
# in that list (the "index"), which is the same for the same file.

def to_display(page, bbox):
    """Box of the page as stored (unrotated) -> box of the page as displayed (the /Rotate of the page applied), origin top left."""
    r = (fitz.Rect(bbox) * page.rotation_matrix).normalize()
    return [r.x0, r.y0, r.x1, r.y1]


def display_point(m, x, y):
    """(x, y) of the page as stored -> of the page as displayed, with the matrix m = (a, b, c, d, e, f) of page.rotation_matrix."""
    return [round(m[0] * x + m[2] * y + m[4], 2), round(m[1] * x + m[3] * y + m[5], 2)]


def quad_corners(q):
    return [q[0], q[1], q[3], q[2], q[0]] if len(q) == 4 else list(q)


def polylines_of(page, d, limit=600):
    m = tuple(page.rotation_matrix)
    """The strokes of a path as polylines of the DISPLAYED page (curves flattened), for the preview that hides a deleted object. At most <limit> points."""
    lines, count = [], 0
    for item in d["items"]:
        kind = item[0]
        if kind == "l":
            pts = [item[1], item[2]]
        elif kind == "c":
            pts = list(bezier_points(item[1], item[2], item[3], item[4], 8))
        elif kind == "re":
            r = item[1]
            pts = [(r[0], r[1]), (r[2], r[1]), (r[2], r[3]), (r[0], r[3]), (r[0], r[1])]
        elif kind == "qu":
            pts = quad_corners(item[1])
        else:
            continue
        lines.append([display_point(m, p[0], p[1]) for p in pts])
        count += len(pts)
        if count >= limit:
            break
    return lines


def kind_of(d):
    items = d["items"]
    return "line" if d.get("fill") is None and all(i[0] == "l" for i in items) else "shape"


def object_json(page, index, d):
    return {"kind": kind_of(d), "index": index, "bbox": to_display(page, d["rect"]), "width": d.get("width") or 0, "paths": polylines_of(page, d)}


def image_objects(page):
    result = []
    if not page.get_images(full=True):
        return result
    for k, info in enumerate(page.get_image_info(xrefs=True)):
        r = fitz.Rect(info["bbox"])
        if not r.is_empty:
            result.append({"kind": "image", "index": k, "bbox": to_display(page, r), "width": 0, "paths": [], "xref": info.get("xref", 0), "_rect": r})
    return result


def bezier_points(p1, p2, p3, p4, steps=16):
    for k in range(steps + 1):
        t = k / steps
        a, b, c, d = (1 - t) ** 3, 3 * (1 - t) ** 2 * t, 3 * (1 - t) * t * t, t ** 3
        yield (a * p1[0] + b * p2[0] + c * p3[0] + d * p4[0], a * p1[1] + b * p2[1] + c * p3[1] + d * p4[1])


def distance_to_segment(px, py, ax, ay, bx, by):
    dx, dy = bx - ax, by - ay
    length2 = dx * dx + dy * dy
    t = 0 if length2 == 0 else max(0, min(1, ((px - ax) * dx + (py - ay) * dy) / length2))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def near_path(items, x, y, reach):
    """True when the point is within <reach> of any piece of the path (lines, curves, rectangles, quads)."""
    for item in items:
        kind = item[0]
        if kind == "l":
            if distance_to_segment(x, y, item[1][0], item[1][1], item[2][0], item[2][1]) <= reach:
                return True
        elif kind == "c":
            pts = list(bezier_points(item[1], item[2], item[3], item[4]))
            if any(distance_to_segment(x, y, *pts[k], *pts[k + 1]) <= reach for k in range(len(pts) - 1)):
                return True
        elif kind in ("re", "qu"):
            corners = [(item[1][0], item[1][1]), (item[1][2], item[1][1]), (item[1][2], item[1][3]), (item[1][0], item[1][3]), (item[1][0], item[1][1])] if kind == "re" else quad_corners(item[1])
            if any(distance_to_segment(x, y, *corners[k], *corners[k + 1]) <= reach for k in range(len(corners) - 1)):
                return True
    return False


def job_pick(job):
    doc = fitz.open(job["path"])
    page = doc[job["page"] - 1]
    point = fitz.Point(job["x"], job["y"]) * page.derotation_matrix
    x, y, tol = point.x, point.y, job.get("tol", 2.5)
    hits = []
    for index, d in enumerate(page.get_cdrawings()):
        x0, y0, x1, y1 = d["rect"]
        reach = tol + (d.get("width") or 0) / 2
        if x < x0 - reach or x > x1 + reach or y < y0 - reach or y > y1 + reach:
            continue
        kind = kind_of(d)
        if kind == "line":
            if not any(distance_to_segment(x, y, i[1][0], i[1][1], i[2][0], i[2][1]) <= reach for i in d["items"]):
                continue
        elif d.get("fill") is None and not near_path(d["items"], x, y, reach):
            continue  # an outline (a frame, handwriting) is hit on its stroke only; a filled shape anywhere inside
        hits.append((index, d, kind))
    for img in image_objects(page):
        if img["_rect"].contains(fitz.Point(x, y)):
            hits.append((img, None, "image"))
    # smallest first: a line over a picture, a small shape inside a big one
    def area(h):
        r = h[0]["_rect"] if h[2] == "image" else fitz.Rect(h[1]["rect"])
        return (h[2] == "image", r.width * r.height)
    hits.sort(key=area)
    objects = [h[0] if h[2] == "image" else object_json(page, h[0], h[1]) for h in hits[:8]]
    for o in objects:
        o.pop("_rect", None)
    emit(type="pick", page=job["page"], width=page.rect.width, height=page.rect.height, rotation=page.rotation, objects=objects)


def job_pick_area(job):
    doc = fitz.open(job["path"])
    page = doc[job["page"] - 1]
    x0, y0, x1, y1 = job["rect"]
    box = (fitz.Rect(x0, y0, x1, y1) * page.derotation_matrix).normalize()
    bx0, by0, bx1, by1 = box.x0, box.y0, box.x1, box.y1
    found = []
    for index, d in enumerate(page.get_cdrawings()):
        r = d["rect"]
        if r[0] >= bx0 and r[1] >= by0 and r[2] <= bx1 and r[3] <= by1:
            found.append(object_json(page, index, d))
            if len(found) >= 5000:
                break
    for img in image_objects(page):
        if box.contains(img["_rect"]):
            img.pop("_rect", None)
            found.append(img)
    emit(type="pick", page=job["page"], width=page.rect.width, height=page.rect.height, rotation=page.rotation, objects=found)


_doc_cache = {}


def _open_cached(path):
    doc = _doc_cache.get(path)
    if doc is None:
        doc = _doc_cache[path] = fitz.open(path)
    return doc


def area_objects_of_page(args):
    """The objects of ONE page in the rectangle (fractions of the page): the centre inside it (a stroke the area cuts a little still counts), or also every one that only touches it.
    A function of its own so that a pool of processes can take pages one by one."""
    path, number, rect, touch = args
    doc = _open_cached(path)
    u1, v1, u2, v2 = rect
    page = doc[number - 1]
    w, h = page.rect.width, page.rect.height
    box = (fitz.Rect(u1 * w, v1 * h, u2 * w, v2 * h) * page.derotation_matrix).normalize()  # the area is drawn on the displayed page
    bx0, by0, bx1, by1 = box.x0, box.y0, box.x1, box.y1
    barea = box.width * box.height
    found = []
    for index, d in enumerate(page.get_cdrawings()):
        x0, y0, x1, y1 = d["rect"]
        # most paths are nowhere near the area: decided from the numbers alone
        if x1 < bx0 - 0.01 or x0 > bx1 + 0.01 or y1 < by0 - 0.01 or y0 > by1 + 0.01:
            continue
        inside = x0 >= bx0 and y0 >= by0 and x1 <= bx1 and y1 <= by1
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        centre = bx0 <= cx <= bx1 and by0 <= cy <= by1
        if not inside and not centre and not touch:
            continue
        if (x1 - x0) * (y1 - y0) > 3 * barea and not inside:
            continue  # a frame or a long rule around / across the area is not what was drawn around
        found.append(object_json(page, index, d))
        if len(found) >= 5000:
            break
    for img in image_objects(page):
        r = img["_rect"]
        if r.width * r.height > 0.5 * w * h and not (r.x0 >= bx0 and r.y0 >= by0 and r.x1 <= bx1 and r.y1 <= by1):
            continue  # a scan of the whole page is not "the signature"
        centre = fitz.Point((r.x0 + r.x1) / 2, (r.y0 + r.y1) / 2)
        if box.contains(r) or box.contains(centre) or (touch and box.intersects(r + (-0.01, -0.01, 0.01, 0.01))):
            img.pop("_rect", None)
            found.append(img)
    return {"page": number, "width": w, "height": h, "rotation": page.rotation, "objects": found[:5000]}


def job_area_objects(job):
    """The drawn objects of a rectangle on each asked page. Many pages are shared among several processes (a page of a drawing takes 0.1-0.5 s), each page reported as it is done."""
    pages = job["pages"]
    tasks = [(job["path"], number, job["rect"], job.get("touch", False)) for number in pages]
    workers = min(len(tasks) // 6, (os.cpu_count() or 2) - 1, 6)
    if workers >= 2:
        try:
            import multiprocessing
            with multiprocessing.get_context("spawn").Pool(workers) as pool:
                for result in pool.imap(area_objects_of_page, tasks, chunksize=2):
                    emit(type="areaObjects", **result)
            return
        except Exception:
            pass  # no processes here: one after the other
    for task in tasks:
        emit(type="areaObjects", **area_objects_of_page(task))


def redraw(page, d):
    shape = page.new_shape()
    for item in d["items"]:
        if item[0] == "l":
            shape.draw_line(fitz.Point(item[1]), fitz.Point(item[2]))
        elif item[0] == "c":
            shape.draw_bezier(fitz.Point(item[1]), fitz.Point(item[2]), fitz.Point(item[3]), fitz.Point(item[4]))
        elif item[0] == "re":
            shape.draw_rect(fitz.Rect(item[1]))
        elif item[0] == "qu":
            shape.draw_quad(fitz.Quad(item[1]))
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
    total = len(by_page)
    for done, (number, targets) in enumerate(sorted(by_page.items())):
        page = doc[number - 1]
        drawings = page.get_cdrawings()
        wanted = {t["index"] for t in targets if t["kind"] != "image" and t["index"] < len(drawings)}
        infos = page.get_image_info(xrefs=True)
        images = [fitz.Rect(infos[t["index"]]["bbox"]) for t in targets if t["kind"] == "image" and t["index"] < len(infos)]
        boxes = []
        for index in wanted:
            d = drawings[index]
            x0, y0, x1, y1 = d["rect"]
            # MuPDF decides "covered" with the stroked bounds of the path (curve hull + miter joins: up to width x 10), not with the box of the points
            reach = max(0.8, (d.get("width") or 0) * 10 + 1)
            box = (x0 - reach, y0 - reach, x1 + reach, y1 + reach)
            boxes.append(box)
            page.add_redact_annot(fitz.Rect(box), fill=False)
            removed += 1
        for image in images:
            page.add_redact_annot(image, fill=False)
            removed += 1
        # paths that are not deleted but lie inside a deleted path's box would go with it: they are found from the numbers (only the area the boxes cover is looked at)
        spared = []
        if boxes:
            ux0, uy0 = min(b[0] for b in boxes), min(b[1] for b in boxes)
            ux1, uy1 = max(b[2] for b in boxes), max(b[3] for b in boxes)
            for k, other in enumerate(drawings):
                r = other["rect"]
                if k in wanted or r[0] < ux0 or r[1] < uy0 or r[2] > ux1 or r[3] > uy1:
                    continue
                if any(r[0] >= b[0] and r[1] >= b[1] and r[2] <= b[2] and r[3] <= b[3] for b in boxes):
                    spared.append(k)
        page.apply_redactions(images=fitz.PDF_REDACT_IMAGE_REMOVE if images else fitz.PDF_REDACT_IMAGE_NONE,
                              graphics=fitz.PDF_REDACT_LINE_ART_REMOVE_IF_COVERED if wanted else fitz.PDF_REDACT_LINE_ART_NONE,
                              text=fitz.PDF_REDACT_TEXT_NONE)
        for k in spared:
            redraw(page, drawings[k])
        emit(type="progress", done=done + 1, total=total, page=number)
    doc.saveIncr()
    emit(type="done", removed=removed)


def main():
    with open(sys.argv[1], encoding="utf-8") as handle:
        job = json.load(handle)
    try:
        {"runs": job_runs, "apply": job_apply, "area": job_area, "pick": job_pick, "pickArea": job_pick_area, "areaObjects": job_area_objects, "delete": job_delete}[job["mode"]](job)
    except Exception as ex:  # report, the caller shows it
        import traceback
        sys.stderr.write(traceback.format_exc())
        emit(type="error", message=str(ex))


if __name__ == "__main__":
    main()
