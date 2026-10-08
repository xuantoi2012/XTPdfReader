# OCR of scanned PDF pages for PDF Reader Pro. Run by Services/OcrService.cs with the same embedded Python as the MuPDF worker:
#   python -u OcrWorker.py job.json
# One JSON object per output line. Words are given as fractions (0..1) of the DISPLAYED page (rotation applied, origin top left),
# so the C# side can place them on any /Rotate page. The file is only read, never changed.
import json
import math
import os
import sys
import time

import pymupdf as fitz


def emit(**message):
    sys.stdout.write(json.dumps(message, ensure_ascii=False) + "\n")
    sys.stdout.flush()


def has_text(page, min_chars):
    return len("".join(page.get_text("text").split())) >= min_chars


def words_of(raw, dpi):
    """Words of a one-page OCR PDF as (text, x0, y0, x1, y1) in pixels of the picture that was read.
    MuPDF lays the characters of a word side by side and puts the whole gap in front of the next word's first character, but only
    sometimes writes a space there (with Vietnamese letters it often does not), so a gap is also a word boundary."""
    k = dpi / 72.0
    result = []
    for block in raw["blocks"]:
        for line in block.get("lines", []):
            chars = [c for span in line["spans"] for c in span["chars"]]
            text, box, previous = "", None, None
            for c in chars:
                x0, y0, x1, y1 = c["bbox"]
                if c["c"] == " " or (previous is not None and x0 - previous > 0.3):
                    if text:
                        result.append((text, *[v * k for v in box]))
                        text, box = "", None
                    if c["c"] == " ":
                        previous = x1
                        continue
                text += c["c"]
                box = (x0, y0, x1, y1) if box is None else (min(box[0], x0), min(box[1], y0), max(box[2], x1), max(box[3], y1))
                previous = x1
            if text:
                result.append((text, *[v * k for v in box]))
    return result


def ocr_page(page, language, tessdata, dpi, tile, overlap):
    """OCR of one page in tiles (tesseract reads text of 20-60 px best, and a drawing sheet at 300 dpi is many thousand pixels wide).
    A word belongs to the tile that holds its centre, away from the overlapping strip, so nothing is read twice."""
    rect = page.rect
    scale = dpi / 72.0
    width, height = rect.width * scale, rect.height * scale
    step = max(1, tile - overlap)
    xs = list(range(0, max(1, int(math.ceil(width - overlap))), step)) or [0]
    ys = list(range(0, max(1, int(math.ceil(height - overlap))), step)) or [0]
    words = []
    for ty in ys:
        for tx in xs:
            tw, th = min(tile, int(math.ceil(width)) - tx), min(tile, int(math.ceil(height)) - ty)
            if tw < 8 or th < 8:
                continue
            clip = fitz.Rect(tx / scale, ty / scale, (tx + tw) / scale, (ty + th) / scale)
            pix = page.get_pixmap(matrix=fitz.Matrix(scale, scale), clip=clip, colorspace=fitz.csGRAY, alpha=False)
            if pix.is_unicolor:
                continue
            # the picture goes on a scratch page of its natural size, which is then read (pix.pdfocr_tobytes writes invisible text that
            # text extraction does not return, get_textpage_ocr gives the characters directly)
            scratch = fitz.open()
            sheet = scratch.new_page(width=pix.width * 72.0 / dpi, height=pix.height * 72.0 / dpi)
            sheet.insert_image(sheet.rect, pixmap=pix)
            text_page = sheet.get_textpage_ocr(language=language, dpi=dpi, full=True, tessdata=tessdata)
            raw = sheet.get_text("rawdict", textpage=text_page)
            half = overlap / 2.0
            left = tx + (half if tx > 0 else 0)
            right = tx + tw - (half if tx + tw < width - 1 else 0)
            top = ty + (half if ty > 0 else 0)
            bottom = ty + th - (half if ty + th < height - 1 else 0)
            for text, x0, y0, x1, y1 in words_of(raw, dpi):
                cx, cy = tx + (x0 + x1) / 2, ty + (y0 + y1) / 2
                if not (left <= cx < right and top <= cy < bottom):
                    continue
                if not text.strip("|_~`'\".,;:-"):
                    continue  # a ruling line read as punctuation
                words.append([text, (tx + x0) / width, (ty + y0) / height, (tx + x1) / width, (ty + y1) / height])
            scratch.close()
    return words, width, height


def main():
    job = json.load(open(sys.argv[1], encoding="utf-8"))
    document = fitz.open(job["path"])
    pages = job.get("pages") or list(range(document.page_count))
    dpi = int(job.get("dpi", 300))
    emit(type="start", pageCount=document.page_count, pages=len(pages))
    for index in pages:
        started = time.time()
        try:
            page = document[index]
            if job.get("skipText", True) and has_text(page, int(job.get("minChars", 20))):
                emit(type="page", page=index, skipped=True, words=[], ms=0)
                continue
            words, width, height = ocr_page(page, job.get("language", "vie"), job["tessdata"], dpi, int(job.get("tile", 3200)), int(job.get("overlap", 160)))
            emit(type="page", page=index, skipped=False, words=words, widthPx=width, heightPx=height, ms=int((time.time() - started) * 1000))
        except Exception as error:  # one bad page must not stop the rest
            emit(type="error", page=index, message=str(error))
    emit(type="done")


if __name__ == "__main__":
    main()
