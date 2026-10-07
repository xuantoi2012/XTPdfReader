"""Persistent multi-page trial worker. JSON header followed by RGB bytes."""
import json
import os
import re
from collections import OrderedDict
from pathlib import Path
import sys
import time
import pymupdf as fitz

# Resolve unembedded TrueType fonts against the Windows font registry.
# Font streams are attached only to the worker's in-memory document; never saved.
def font_key(name):
    name = re.sub(r"^[A-Z]{6}\+", "", name)
    name = re.sub(r"\s*\(TrueType\)\s*$", "", name, flags=re.I)
    name = name.replace("PSMT", "").replace("PS-", "").removesuffix("MT")
    return re.sub(r"[^a-z0-9]", "", name.lower()).removesuffix("regular")

def windows_fonts():
    found = {}
    if os.name != "nt":
        return found
    import winreg
    for root in (winreg.HKEY_LOCAL_MACHINE, winreg.HKEY_CURRENT_USER):
        try:
            with winreg.OpenKey(root, r"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts") as key:
                for index in range(winreg.QueryInfoKey(key)[1]):
                    name, file, _ = winreg.EnumValue(key, index)
                    path = Path(file)
                    if not path.is_absolute():
                        path = Path(os.environ.get("WINDIR", r"C:\Windows")) / "Fonts" / path
                    if path.suffix.lower() == ".ttf" and path.is_file():
                        found[font_key(name)] = path
        except OSError:
            pass
    return found

system_fonts = windows_fonts()

def resolve_unembedded_fonts(document, page):
    streams = getattr(document, "_xt_system_font_streams", None)
    if streams is None:
        streams = document._xt_system_font_streams = {}
    # Only inspect resources of the requested page. Scanning an entire large PDF
    # on each worker's first request would delay opening and waste network reads.
    for xref in {font[0] for font in page.get_fonts(full=True) if font[0] > 0}:
        if document.xref_get_key(xref, "Subtype") != ("name", "/TrueType"):
            continue
        kind, descriptor = document.xref_get_key(xref, "FontDescriptor")
        if kind != "xref":
            continue
        descriptor = int(descriptor.split()[0])
        if any(document.xref_get_key(descriptor, key)[0] != "null" for key in ("FontFile", "FontFile2", "FontFile3")):
            continue
        name = document.xref_get_key(xref, "BaseFont")[1].lstrip("/")
        path = system_fonts.get(font_key(name))
        if path is None:
            continue
        if path not in streams:
            data = path.read_bytes()
            stream = document.get_new_xref()
            document.update_object(stream, f"<< /Length1 {len(data)} >>")
            document.update_stream(stream, data)
            streams[path] = stream
        document.xref_set_key(descriptor, "FontFile2", f"{streams[path]} 0 R")

# MuPDF can write BGR directly: WPF's pixel order, so no per-pixel channel swap (and the render itself is a little faster than RGB).
BGR = fitz.Colorspace(fitz.mupdf.FzColorspace(fitz.mupdf.FzColorspace.Fixed_BGR))

def squeeze(text, match_case):
    # Cheap pre-filter key: whitespace-insensitive, case-insensitive unless asked otherwise.
    text = re.sub(r"\s+", "", text)
    return text if match_case else text.lower()

search_stats = {}

def search_page(document, page, data, use_filter=True):
    """Plain text is ~5-10x cheaper than rawdict: only pages that can contain the query pay for per-character boxes.
    When most pages match, the filter is pure overhead and the caller switches it off."""
    if use_filter:
        plain = page.get_text("text")
        has_text = bool(plain.strip())
        if not has_text or squeeze(data["query"], data["matchCase"]) not in squeeze(plain, data["matchCase"]):
            return dict(hasText=has_text, matches=[])
    resolve_unembedded_fonts(document, page)  # same fonts as the rendering so highlight boxes line up
    chars = characters(page)
    text = "".join(c for c, _ in chars)
    pattern = re.escape(data["query"])
    if data["wholeWord"]:
        pattern = r"(?<!\w)" + pattern + r"(?!\w)"
    matches = []
    for match in re.finditer(pattern, text, 0 if data["matchCase"] else re.IGNORECASE):
        matches.append(dict(snippet=text[max(0, match.start()-35):match.end()+35].replace("\n", " "),
                            rects=rectangles(page, chars[match.start():match.end()])))
        if len(matches) >= 200:
            break
    return dict(hasText=bool(text.strip()), matches=matches)

documents = OrderedDict()
signature_appearances = {}
lists = OrderedDict()
rasters = OrderedDict()
raster_bytes = 0
list_limit = int(os.environ.get("XTPDF_MUPDF_LISTS", "64"))
document_limit = int(os.environ.get("XTPDF_MUPDF_DOCUMENTS", "64"))
raster_limit = 0 if os.environ.get("XTPDF_MUPDF_NO_RASTER_CACHE") == "1" else 512 * 1024 * 1024
normal_list_limit = list_limit
normal_document_limit = document_limit
last_rendered_key = None

def layer_index_by_xref(pdf, ocgs):
    """Map OCG xref -> the index pdf_enable_layer expects.

    MuPDF keeps its own layer list whose order is NOT the /OCGs array order (nor /D/Order) on real CAD files, so indexing by
    get_ocgs() order toggles the wrong layers. Probe it: for each bit of the index enable the layers having that bit set and read
    every OCG's hidden state. The result is verified against pdf_layer_name; None = could not be trusted.
    """
    count = fitz.mupdf.pdf_count_layers(pdf)
    xrefs = list(ocgs)
    if count == 0 or count != len(xrefs):
        return None
    stack = fitz.mupdf.PdfResourceStack()
    objects = {x: fitz.mupdf.pdf_new_indirect(pdf, x, 0) for x in xrefs}
    index = {x: 0 for x in xrefs}
    try:
        for bit in range(max(1, (count - 1).bit_length())):
            for i in range(count):
                fitz.mupdf.pdf_enable_layer(pdf, i, (i >> bit) & 1)
            for x in xrefs:
                if not fitz.mupdf.pdf_is_ocg_hidden(pdf, stack, "View", objects[x]):
                    index[x] |= 1 << bit
    finally:
        for i in range(count):
            fitz.mupdf.pdf_enable_layer(pdf, i, 1)
    if len(set(index.values())) != count or any(i >= count for i in index.values()):
        return None
    for x, i in index.items():
        if fitz.mupdf.pdf_layer_name(pdf, i) != ocgs[x]["name"]:
            return None
    return index


def apply_hidden_layers(opened, hidden):
    ocgs = opened.get_ocgs()
    off = {int(identifier.split()[0]) for identifier in hidden}
    pdf = fitz._as_pdf_document(opened)
    index = layer_index_by_xref(pdf, ocgs)
    if index is None:
        # Probe failed: fall back to the OCG array order (right for files whose layer list is in array order).
        index = {xref: i for i, xref in enumerate(ocgs)}
    for xref, i in index.items():
        fitz.mupdf.pdf_enable_layer(pdf, i, int(xref not in off))


def send(value):
    sys.stdout.buffer.write((json.dumps(value) + "\n").encode("utf-8"))
    sys.stdout.buffer.flush()

def close_document(stamp):
    global raster_bytes
    for key in list(lists):
        if key[0] == stamp:
            del lists[key]
    for key in list(signature_appearances):
        if key[0] == stamp:
            del signature_appearances[key]
    for key in list(rasters):
        if key[0] == stamp:
            raster_bytes -= len(rasters.pop(key)[1])
    documents.pop(stamp).close()

def characters(page):
    chars = []
    for block in page.get_text("rawdict")["blocks"]:
        for line in block.get("lines", []):
            for span in line["spans"]:
                chars.extend((c["c"], c["bbox"]) for c in span["chars"])
            chars.append(("\n", None))
    return chars

def rectangles(page, chars):
    result = []
    for _, box in chars:
        if box is None:
            continue
        rect = fitz.Rect(box) * page.rotation_matrix
        result.append([rect.x0 / page.rect.width, rect.y0 / page.rect.height,
                       rect.x1 / page.rect.width, rect.y1 / page.rect.height])
    return result

for line in sys.stdin:
    try:
        request = json.loads(line)
        memory_state = request.get("memoryState", 0)
        configured_lists = max(1, min(64, request.get("nativeListLimit", normal_list_limit)))
        list_limit = configured_lists if memory_state == 0 else min(configured_lists, 2 if memory_state == 1 else 1)
        document_limit = normal_document_limit if memory_state == 0 else min(normal_document_limit, 1)
        if request.get("op") == "memory":
            protected = {(str(Path(p["path"]).resolve()).casefold(), p["page"]) for p in (request.get("data") or [])}
            protected_paths = {p[0] for p in protected}
            # Keep native resources for the visible/presentation pages even if they exceed the target.
            for old in list(documents):
                if old[0].casefold() not in protected_paths:
                    close_document(old)
            for key in list(lists):
                if (key[0][0].casefold(), key[1]) not in protected:
                    del lists[key]
            rasters.clear()
            raster_bytes = 0
            fitz.TOOLS.store_shrink(100 if memory_state == 2 else 50)
            send(dict(ok=True, documents=len(documents), displayLists=len(lists), storeBytes=fitz.TOOLS.store_size()))
            continue
        if request.get("op") == "stats":
            send(dict(documents=len(documents), displayLists=len(lists), rasterBytes=raster_bytes,
                      storeBytes=fitz.TOOLS.store_size()))
            continue
        if request.get("op") in ("close", "release"):
            active = {str(Path(p).resolve()).casefold() for p in (request.get("data") or [])}
            closing = str(Path(request["path"]).resolve()).casefold() if request["op"] == "close" else None
            for old in list(documents):
                if (closing is not None and old[0].casefold() != closing) or (closing is None and old[0].casefold() in active):
                    continue
                close_document(old)
            fitz.TOOLS.store_shrink(100)
            send(dict(ok=True))
            continue
        source = Path(request["path"])
        hidden = request.get("hidden")
        stamp = (str(source.resolve()), source.stat().st_mtime_ns, source.stat().st_size,
                 request.get("password") or "", None if hidden is None else tuple(sorted(hidden)))
        op = request.get("op")
        raster_key = None if op else (stamp, request["page"], request["fullWidth"], request["fullHeight"],
                                      tuple(request["rect"]), request.get("annotations", False), request.get("alpha", False))
        if raster_key in rasters:
            header, data = rasters[raster_key]
            rasters.move_to_end(raster_key)
            sys.stdout.buffer.write((json.dumps(dict(header, prepareMs=0, renderMs=0, cached=True)) + "\n").encode("utf-8"))
            sys.stdout.buffer.write(data)
            sys.stdout.buffer.flush()
            continue
        prepare = time.perf_counter()
        for old in list(documents):
            if old[0] == stamp[0] and old != stamp:
                close_document(old)
        if stamp not in documents:
            opened = fitz.open(str(source))
            if opened.needs_pass and not opened.authenticate(stamp[3]):
                opened.close()
                raise ValueError("Password required or incorrect")
            if hidden is not None:
                apply_hidden_layers(opened, hidden)
            documents[stamp] = opened
        document = documents[stamp]
        documents.move_to_end(stamp)
        while len(documents) > document_limit:
            close_document(next(iter(documents)))
            fitz.TOOLS.store_shrink(100)
        if op == "metadata":
            send(dict(count=len(document), sizes=[[p.rect.width, p.rect.height] for p in document]))
            continue
        if op == "searchrange":
            # Several pages per round trip; the reply is one JSON line.
            first, count = request["page"], request["data"]["count"]
            results = []
            stat_key = (stamp[0], request["data"]["query"], request["data"]["matchCase"], request["data"]["wholeWord"])
            stat = search_stats.setdefault(stat_key, [0, 0])
            if first == 0:
                stat[0] = stat[1] = 0
            for index in range(first, min(len(document), first + count)):
                use_filter = stat[0] < 4 or stat[1] / stat[0] < 0.6
                result = search_page(document, document[index], request["data"], use_filter)
                result["page"] = index
                stat[0] += 1
                stat[1] += 1 if result["matches"] else 0
                results.append(result)
            send(dict(pages=results))
            continue
        if op in ("search", "select"):
            page = document[request["page"]]
            resolve_unembedded_fonts(document, page)
            chars = characters(page)
            text = "".join(c for c, _ in chars)
            data = request["data"]
            if op == "search":
                pattern = re.escape(data["query"])
                if data["wholeWord"]:
                    pattern = r"(?<!\w)" + pattern + r"(?!\w)"
                matches = []
                for match in re.finditer(pattern, text, 0 if data["matchCase"] else re.IGNORECASE):
                    matches.append(dict(snippet=text[max(0, match.start()-35):match.end()+35].replace("\n", " "),
                                        rects=rectangles(page, chars[match.start():match.end()])))
                    if len(matches) >= 200:
                        break
                send(dict(hasText=bool(text.strip()), matches=matches))
            else:
                points = [fitz.Point(data["ax"], data["ay"]) * page.transformation_matrix,
                          fitz.Point(data["bx"], data["by"]) * page.transformation_matrix]
                indices = []
                for point in points:
                    nearest = [(max(b[0]-point.x, 0, point.x-b[2])**2 + max(b[1]-point.y, 0, point.y-b[3])**2, i)
                               for i, (_, b) in enumerate(chars) if b is not None]
                    if not nearest:
                        break
                    distance, index = min(nearest)
                    if distance > 400:
                        break
                    indices.append(index)
                chosen = chars[min(indices):max(indices)+1] if len(indices) == 2 else []
                send(dict(text="".join(c for c, _ in chosen), rects=rectangles(page, chosen)))
            continue
        annotations = request.get("annotations", False)
        key = (stamp, request["page"], annotations)
        if key not in lists:
            page = document[request["page"]]
            resolve_unembedded_fonts(document, page)
            widget_key = (stamp, request["page"])
            if widget_key not in signature_appearances:
                signatures = {widget.xref for widget in (page.widgets() or [])
                              if widget.field_type == fitz.PDF_WIDGET_TYPE_SIGNATURE and not widget.is_signed
                              and document.xref_get_key(widget.xref, "AP/N")[0] == "xref"}
                signature_appearances[widget_key] = signatures
                # MuPDF skips unsigned signature widgets even when they have an AP.
                # Treat only those AP-bearing widgets as stamps in this private
                # render document. Never save these compatibility changes to disk.
                for xref in signatures:
                    document.xref_set_key(xref, "Subtype", "/Stamp")
                if signatures:
                    page = document.reload_page(page)
            signatures = signature_appearances[widget_key]
            if not annotations and (page.first_widget is not None or signatures):
                native_list = fitz.mupdf.FzDisplayList(fitz.mupdf.fz_bound_page(page.this))
                device = fitz.mupdf.fz_new_list_device(native_list)
                matrix = fitz.mupdf.FzMatrix(1, 0, 0, 1, 0, 0)
                cookie = fitz.mupdf.FzCookie()
                fitz.mupdf.fz_run_page_contents(page.this, device, matrix, cookie)
                fitz.mupdf.fz_run_page_widgets(page.this, device, matrix, cookie)
                annot = fitz.mupdf.pdf_first_annot(fitz._as_pdf_page(page))
                while annot.m_internal:
                    if fitz.mupdf.pdf_to_num(fitz.mupdf.pdf_annot_obj(annot)) in signatures:
                        fitz.mupdf.pdf_run_annot(annot, device, matrix, cookie)
                    annot = fitz.mupdf.pdf_next_annot(annot)
                fitz.mupdf.fz_close_device(device)
                lists[key] = fitz.DisplayList(native_list)
            else:
                lists[key] = page.get_displaylist(annots=annotations)
            while len(lists) > list_limit:
                lists.popitem(last=False)
        lists.move_to_end(key)
        display = lists[key]
        prepare_ms = (time.perf_counter() - prepare) * 1000
        fullw, fullh = request["fullWidth"], request["fullHeight"]
        x, y, w, h = request["rect"]
        pw, ph = display.rect.width, display.rect.height
        if fullh == 0:
            fullh = max(1, round(fullw * ph / pw))
            x, y, w, h = 0, 0, fullw, fullh
        if fullw <= 0 or fullh <= 0 or w <= 0 or h <= 0 or w * h > 64 * 1024 * 1024:
            raise ValueError("Invalid or oversized raster request; use viewport crops for very long pages")
        start = time.perf_counter()
        alpha = request.get("alpha", False)
        channels = 4 if alpha else 3
        pix = display.get_pixmap(matrix=fitz.Matrix(fullw / pw, fullh / ph),
            clip=fitz.Rect(x * pw / fullw, y * ph / fullh,
                           (x + w) * pw / fullw, (y + h) * ph / fullh),
            colorspace=BGR, alpha=alpha)
        render_ms = (time.perf_counter() - start) * 1000
        data = pix.samples
        # Float clip rounding can add a row/column. Keep the requested global
        # pixel origin, not just the bitmap dimensions, when extracting the crop.
        if (pix.x, pix.y, pix.width, pix.height) != (x, y, w, h):
            normalized = bytearray(b'\x00' if alpha else b'\xff') * (w * h * channels)
            left, right = max(x, pix.x), min(x + w, pix.x + pix.width)
            top, bottom = max(y, pix.y), min(y + h, pix.y + pix.height)
            if right > left:
                for row in range(top, bottom):
                    src = (row - pix.y) * pix.stride + (left - pix.x) * channels
                    dst = (row - y) * w * channels + (left - x) * channels
                    normalized[dst:dst + (right-left)*channels] = data[src:src + (right-left)*channels]
            data = normalized
            normalized = None
        header = dict(width=w, height=h, stride=w*channels, format="bgra" if alpha else "bgr",
                      length=len(data), prepareMs=prepare_ms, renderMs=render_ms)
        rasters[raster_key] = (header, data)
        raster_bytes += len(data)
        while raster_bytes > raster_limit:
            raster_bytes -= len(rasters.popitem(last=False)[1][1])
        del pix
        display = page = None
        # PyMuPDF 1.28.2 reports store_size as None. Purge on page transitions
        # rather than treating the unavailable measurement as zero.
        if key != last_rendered_key:
            fitz.TOOLS.store_shrink(50 if memory_state == 0 else 100)
        last_rendered_key = key
        sys.stdout.buffer.write((json.dumps(header) + "\n").encode("utf-8"))
        sys.stdout.buffer.write(data)
        sys.stdout.buffer.flush()
        del data
    except Exception as error:
        sys.stdout.buffer.write((json.dumps(dict(error=str(error))) + "\n").encode("utf-8"))
        sys.stdout.buffer.flush()
lists.clear()
for document in documents.values():
    document.close()
