"""A/B native and Python text/object jobs, including real saves on temporary copies."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

import pymupdf as fitz

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent
NATIVE = [str(HERE / "bin" / "xtpdfworker.exe"), "--textedit"]
PYTHON = [sys.executable, str(REPO / "Services" / "TextEdit" / "TextEditWorker.py")]


def run(command, job, folder):
    jobfile = Path(folder) / "edit-job.json"
    jobfile.write_text(json.dumps(job), encoding="utf-8")
    result = subprocess.run(command + [str(jobfile)], capture_output=True, timeout=60,
                            env=dict(os.environ, PYTHONIOENCODING="utf-8"))
    messages = [json.loads(line) for line in result.stdout.decode("utf-8").splitlines() if line.startswith("{")]
    assert result.returncode == 0 and messages, (messages, result.stderr.decode(errors="replace"))
    assert all(m["type"] != "error" for m in messages), messages
    return messages


def fixture(path):
    with fitz.open() as doc:
        for angle in (0, 90, 180, 270):
            page = doc.new_page(width=600, height=400)
            page.draw_rect((40, 40, 340, 300), color=(0, 0, 0), width=2)
            page.draw_line((60, 150), (300, 150), color=(1, 0, 0), width=1)
            page.draw_bezier((80, 220), (100, 160), (180, 260), (250, 220), color=(0, 0, 1))
            page.draw_rect((380, 40, 450, 110), color=(0, 0, 1), fill=(0, 1, 0), width=2)
            page.insert_text((60, 90), "Alpha  Beta", fontsize=14, color=(0, 0, 1))
            page.insert_text((60, 120), "keep this text", fontsize=12)
            image = fitz.Pixmap(fitz.csRGB, fitz.IRect(0, 0, 40, 40), False)
            image.clear_with(80)
            xref = page.insert_image((400, 160, 470, 230), pixmap=image)
            page.insert_image((490, 160, 560, 230), xref=xref)
            # Implicitly closed non-rectangular quad remains line items in PyMuPDF;
            # explicit four-line closure is a quad. A trailing moveto adds no ink.
            for close in (True, False):
                shape = page.new_shape()
                points = [(350, 320), (380, 305), (410, 330), (375, 360)]
                shape.draw_polyline(points if close else points + points[:1])
                shape.finish(closePath=close, color=(0, 0, 0))
                shape.commit()
            stream = doc.get_new_xref()
            doc.update_object(stream, "<<>>")
            doc.update_stream(stream, b"10 10 m 20 20 l 590 390 m S")
            contents = page.get_contents() + [stream]
            doc.xref_set_key(page.xref, "Contents", "[" + " ".join(f"{n} 0 R" for n in contents) + "]")
            page.add_rect_annot((480, 260, 550, 310)).update()
            page.set_rotation(angle)
        doc.save(path)


def compare(a, b):
    assert len(a) == len(b)
    worst = 0.0
    for x, y in zip(a, b):
        assert x["type"] == y["type"] and x["page"] == y["page"]
        if "runs" in x:
            assert len(x["runs"]) == len(y["runs"]), (x["page"], "runs", len(x["runs"]), len(y["runs"]))
            for u, v in zip(x["runs"], y["runs"]):
                for field in ("text", "font", "color", "flags", "bg"):
                    assert u[field] == v[field], (field, u, v)
                for field in ("bbox", "origin"):
                    worst = max(worst, max(abs(i-j) for i, j in zip(u[field], v[field])))
        if "objects" in x:
            pairs_a = [(o["kind"], o["index"]) for o in x["objects"]]
            pairs_b = [(o["kind"], o["index"]) for o in y["objects"]]
            assert pairs_a == pairs_b, (x["page"], "objects", len(pairs_a), len(pairs_b),
                                        next(((u, v) for u, v in zip(pairs_a, pairs_b) if u != v), None))
            for u, v in zip(x["objects"], y["objects"]):
                assert max(abs(i-j) for i, j in zip(u["bbox"], v["bbox"])) < .005, (x["page"], u["index"], u["bbox"], v["bbox"])
                worst = max(worst, max(abs(i-j) for i, j in zip(u["bbox"], v["bbox"])))
    assert worst < 0.005, worst


def main():
    with tempfile.TemporaryDirectory(prefix="xt-edit-ab-") as folder:
        if len(sys.argv) > 1:
            path = str(Path(sys.argv[1]).resolve())
            before = os.stat(path)
            with fitz.open(path) as doc:
                pages = sorted({1, doc.page_count // 2 + 1, doc.page_count})
            for page in pages:
                job = dict(mode="runs", path=path, page=page)
                compare(run(NATIVE, job, folder), run(PYTHON, job, folder))
                print(f"PASS real PDF text runs page {page}", flush=True)
            job = dict(mode="areaObjects", path=path, pages=pages, rect=[0.6, 0.6, 0.95, 0.95], touch=False)
            compare(run(NATIVE, job, folder), run(PYTHON, job, folder))
            after = os.stat(path)
            assert before.st_size == after.st_size and before.st_mtime_ns == after.st_mtime_ns
            print("PASS real PDF object area and unchanged source size/timestamp")
            return
        path = str(Path(folder) / "drawing.pdf")
        fixture(path)
        original = hashlib.sha256(Path(path).read_bytes()).digest()
        jobs = [dict(mode="runs", page=p) for p in range(1, 5)] + [
            dict(mode="area", pages=[1, 2, 3, 4], rect=[0, 0, 1, 1]),
            dict(mode="pickArea", page=1, rect=[0, 0, 600, 400]),
            dict(mode="pick", page=1, x=120, y=150),
            dict(mode="pick", page=1, x=40, y=180),
            dict(mode="pick", page=1, x=420, y=180),
            dict(mode="areaObjects", pages=[1, 2, 3, 4], rect=[0, 0, 1, 1]),
            dict(mode="areaObjects", pages=[1], rect=[0.12, 0.35, 0.5, 0.6], touch=True)]
        for job in jobs:
            compare(run(NATIVE, dict(job, path=path), folder), run(PYTHON, dict(job, path=path), folder))
        assert hashlib.sha256(Path(path).read_bytes()).digest() == original
        print("PASS: run styles/geometry, rotated pages, point/area object selection, read-only queries")
        # Save on copies: remove the frame, curve, and one of two uses of the SAME image.
        for mode in ("apply", "delete"):
            paths = [str(Path(folder) / f"{mode}-{name}.pdf") for name in ("native", "python")]
            for output, command in zip(paths, (NATIVE, PYTHON)):
                shutil.copyfile(path, output)
                if mode == "apply":
                    runs = run(command, dict(mode="runs", path=output, page=1), folder)[0]["runs"]
                    job = dict(mode=mode, path=output, edits=[dict(page=1, bbox=runs[0]["bbox"])])
                else:
                    job = dict(mode=mode, path=output, objects=[dict(page=p, kind=kind, index=index)
                               for p in range(1, 5) for kind, index in (("shape", 0), ("shape", 2), ("image", 0))])
                messages = run(command, job, folder)
                assert messages[-1]["type"] == "done"
            with fitz.open(paths[0]) as native, fitz.open(paths[1]) as python:
                for a, b in zip(native, python):
                    assert a.get_text() == b.get_text()
                    assert a.get_pixmap().samples == b.get_pixmap().samples, (mode, a.number)
                    assert len(a.get_drawings()) == len(b.get_drawings())
                    assert len(a.get_image_info()) == len(b.get_image_info())
            print(f"PASS: {mode} save preserves the same text, images, paths and pixels as Python")


if __name__ == "__main__":
    main()
