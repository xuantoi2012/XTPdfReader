"""Native raster cache protocol regression. Run with a Python containing PyMuPDF."""
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

import pymupdf

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent


def run(command, requests, **settings):
    env = dict(os.environ, **settings)
    wire = b"".join((json.dumps(r) + "\n").encode() for r in requests)
    result = subprocess.run(command, input=wire, capture_output=True, env=env, timeout=60)
    assert result.returncode == 0, result.stderr.decode(errors="replace")
    output = io.BytesIO(result.stdout)
    replies = []
    for request in requests:
        header = json.loads(output.readline())
        assert "error" not in header, (request, header)
        pixels = output.read(header.get("length", 0))
        assert len(pixels) == header.get("length", 0)
        replies.append((header, pixels))
    assert not output.read(), "unexpected protocol output"
    return replies


def main():
    native = [str(HERE / "bin" / "xtpdfworker.exe")]
    reference = [sys.executable, str(REPO / "Tests" / "GpuPdfium" / "MuPdfViewportWorker.py")]
    with tempfile.TemporaryDirectory(prefix="xt-native-cache-") as tmp:
        path = str(Path(tmp) / "layers.pdf")
        with pymupdf.open() as doc:
            layer = doc.add_ocg("Visible red square")
            for index in range(3):
                page = doc.new_page(width=300, height=300)
                page.draw_rect((30, 30, 180, 180), color=(1, 0, 0), fill=(1, 0, 0), oc=layer)
                page.insert_text((20, 230), f"Cache page {index + 1}")
                page.add_text_annot((240, 40), "Cache note")
            doc.save(path)
        base = dict(path=path, page=0, fullWidth=300, fullHeight=0,
                    rect=[0, 0, 300, 0], annotations=True, alpha=False,
                    displayWidth=300, memoryState=0)
        variants = [base, dict(base, annotations=False), dict(base, alpha=True),
                    dict(base, displayWidth=75), dict(base, page=1),
                    dict(base, fullWidth=600, fullHeight=600, rect=[80, 70, 200, 160]),
                    dict(base, hidden=[f"{layer} 0 R"])]
        requests = [r for variant in variants for r in (variant, variant)]
        expected = run(reference, variants, XTPDF_MUPDF_NO_RASTER_CACHE="1")
        actual = run(native, requests + [dict(op="stats")],
                     XTPDF_MUPDF_NO_RASTER_CACHE="0", XTPDF_MUPDF_RASTER_MB="8")
        for index, (header, pixels) in enumerate(expected):
            first, repeated = actual[index * 2:index * 2 + 2]
            assert first[1] == repeated[1] == pixels, f"pixel mismatch variant {index}"
            for field in ("width", "height", "stride", "format", "length"):
                assert first[0][field] == repeated[0][field] == header[field]
        assert expected[0][1] != expected[-1][1], "fixture must visibly change with layers"
        assert actual[-1][0]["rasterHits"] == len(variants)
        fallback = run(native, [base, dict(base, sharedMemory=True)], XTPDF_MUPDF_NO_RASTER_CACHE="0")
        assert all("sharedMemory" not in h and pixels == expected[0][1] for h, pixels in fallback), "shared requests retain binary fallback when the worker raster cache is enabled"

        stats = dict(op="stats")
        # Three 270 KB images fit in 1 MB. Touch page 0, add a fourth key,
        # then page 1 must miss while the recently touched page 0 still hits.
        page1, page2 = dict(base, page=1), dict(base, page=2)
        fourth = dict(base, annotations=False)
        sequence = [base, page1, page2, base, fourth, base, page1, stats]
        bounded = run(native, sequence, XTPDF_MUPDF_NO_RASTER_CACHE="0", XTPDF_MUPDF_RASTER_MB="1")
        assert bounded[-1][0]["rasterHits"] == 2
        assert bounded[-1][0]["rasterBytes"] == 810000
        for action in (dict(op="close", path=path), dict(op="release", data=[]),
                       dict(op="memory", memoryState=1, data=[dict(path=path, page=0)]),
                       dict(op="memory", memoryState=2, data=[dict(path=path, page=0)])):
            replies = run(native, [base, action, stats, base, stats],
                          XTPDF_MUPDF_NO_RASTER_CACHE="0")
            assert replies[2][0]["rasterBytes"] == 0, action
            assert replies[-1][0]["rasterHits"] == 0, action
        for settings in (dict(XTPDF_MUPDF_NO_RASTER_CACHE="1"),
                         dict(XTPDF_MUPDF_NO_RASTER_CACHE="0", XTPDF_MUPDF_RASTER_MB="0.1")):
            replies = run(native, [base, base, stats], **settings)
            assert replies[-1][0]["rasterBytes"] == replies[-1][0]["rasterHits"] == 0
        for state in (1, 2):
            pressure = dict(base, memoryState=state)
            replies = run(native, [base, pressure, pressure, stats], XTPDF_MUPDF_NO_RASTER_CACHE="0")
            assert replies[-1][0]["rasterBytes"] == replies[-1][0]["rasterHits"] == 0
    print("PASS: Python pixel parity, visible layer toggle, cache keys/hits, LRU budget, close/release, memory pressure, disabled/oversized cache")


if __name__ == "__main__":
    main()
