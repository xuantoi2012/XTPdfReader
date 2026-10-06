"""Offscreen native render comparison; page one only, no viewer interaction."""
import argparse
import ctypes as C
import hashlib
import json
from pathlib import Path
import struct
import time
import psutil

p = argparse.ArgumentParser()
p.add_argument("source")
p.add_argument("engine", choices=["pdfium", "mupdf-page", "mupdf-list"])
p.add_argument("--dll")
p.add_argument("--output", required=True)
a = p.parse_args()
out = Path(a.output)
out.mkdir(parents=True, exist_ok=True)
process = psutil.Process()

def memory():
    m = process.memory_info()
    return {"private_mib": m.private / 1048576, "rss_mib": m.rss / 1048576,
            "peak_private_mib": m.peak_pagefile / 1048576}

def emit(**values):
    print(json.dumps(dict(engine=a.engine, **values, **memory())), flush=True)

start = time.perf_counter()
if a.engine == "pdfium":
    lib = C.CDLL(a.dll)
    def bind(name, result, *args):
        f = getattr(lib, name)
        f.restype, f.argtypes = result, args
        return f
    init = bind("FPDF_InitLibrary", None)
    load = bind("FPDF_LoadDocument", C.c_void_p, C.c_char_p, C.c_char_p)
    page_load = bind("FPDF_LoadPage", C.c_void_p, C.c_void_p, C.c_int)
    width = bind("FPDF_GetPageWidthF", C.c_float, C.c_void_p)
    height = bind("FPDF_GetPageHeightF", C.c_float, C.c_void_p)
    create = bind("FPDFBitmap_Create", C.c_void_p, C.c_int, C.c_int, C.c_int)
    fill = bind("FPDFBitmap_FillRect", None, C.c_void_p, C.c_int, C.c_int, C.c_int, C.c_int, C.c_ulong)
    render = bind("FPDF_RenderPageBitmap", None, C.c_void_p, C.c_void_p,
                  C.c_int, C.c_int, C.c_int, C.c_int, C.c_int, C.c_int)
    buffer = bind("FPDFBitmap_GetBuffer", C.c_void_p, C.c_void_p)
    stride = bind("FPDFBitmap_GetStride", C.c_int, C.c_void_p)
    destroy = bind("FPDFBitmap_Destroy", None, C.c_void_p)
    init()
    doc = load(a.source.encode("utf-8"), None)
    if not doc:
        raise RuntimeError("PDFium document load failed")
    page = page_load(doc, 0)
    if not page:
        raise RuntimeError("PDFium page-one load failed")
    pw, ph = width(page), height(page)
else:
    import pymupdf as fitz
    doc = fitz.open(a.source)
    page = doc[0]
    pw, ph = page.rect.width, page.rect.height
    emit(stage="version", version=fitz.version)
emit(stage="open_page_one", ms=(time.perf_counter() - start) * 1000, page_points=[pw, ph])
if a.engine == "mupdf-list":
    start = time.perf_counter()
    display = page.get_displaylist(annots=True)
    emit(stage="build_display_list", ms=(time.perf_counter() - start) * 1000)

# Constant pixel viewport; exact integer crop coordinates at each scale.
sequence = [(3000, 0, "A"), (6000, 0, "B"), (6000, 400, "pan"),
            (6000, 0, "return_B"), (3000, 0, "return_A")]
for cycle in range(3):
    for fullw, dx, label in sequence:
        fullh = round(fullw * ph / pw)
        w, h = min(1920, fullw), min(1080, fullh)
        x = min(fullw - w, (fullw - w) // 2 + dx)
        y = max(0, (fullh - h) // 2)
        start = time.perf_counter()
        if a.engine == "pdfium":
            bitmap = create(w, h, 1)
            if not bitmap:
                raise RuntimeError("Bitmap allocation failed")
            fill(bitmap, 0, 0, w, h, 0xFFFFFFFF)
            render(bitmap, page, -x, -y, fullw, fullh, 0, 1)
            elapsed = (time.perf_counter() - start) * 1000
            raw = C.string_at(buffer(bitmap), stride(bitmap) * h)
            if cycle == 0 and label == "B":
                header = struct.pack("<2sIHHI", b"BM", 54 + len(raw), 0, 0, 54)
                info = struct.pack("<IiiHHIIiiII", 40, w, -h, 1, 32, 0, len(raw), 0, 0, 0, 0)
                (out / "pdfium.bmp").write_bytes(header + info + raw)
            destroy(bitmap)
            dimensions = [w, h]
        else:
            matrix = fitz.Matrix(fullw / pw, fullh / ph)
            clip = fitz.Rect(x * pw / fullw, y * ph / fullh,
                             (x + w) * pw / fullw, (y + h) * ph / fullh)
            if a.engine == "mupdf-list":
                pix = display.get_pixmap(matrix=matrix, clip=clip, colorspace=fitz.csRGB, alpha=0)
            else:
                pix = page.get_pixmap(matrix=matrix, clip=clip, colorspace=fitz.csRGB, alpha=False)
            elapsed = (time.perf_counter() - start) * 1000
            raw = pix.samples
            dimensions = [pix.width, pix.height]
            if cycle == 0 and label == "B":
                pix.save(str(out / (a.engine + ".png")))
            del pix
        emit(stage="render", cycle=cycle, action=label, full_width=fullw,
             crop=[x, y, w, h], dimensions=dimensions, ms=elapsed,
             sha256=hashlib.sha256(raw).hexdigest())
        del raw
if a.engine == "pdfium":
    bind("FPDF_ClosePage", None, C.c_void_p)(page)
    bind("FPDF_CloseDocument", None, C.c_void_p)(doc)
    bind("FPDF_DestroyLibrary", None)()
else:
    doc.close()
