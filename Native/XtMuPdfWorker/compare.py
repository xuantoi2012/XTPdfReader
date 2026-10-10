"""Runs the same requests through the Python worker and the native worker and compares the replies.

    python compare.py <file.pdf> [native.exe] [python worker .py]

Needs PyMuPDF (the app's runtime python). Prints sizes, the time of each request and how far the pictures differ.
"""
import json
import sys
sys.stdout.reconfigure(encoding="utf-8", errors="replace")
import os
import subprocess
import sys
import time

here = os.path.dirname(os.path.abspath(__file__))
repo = os.path.abspath(os.path.join(here, "..", ".."))
pdf = sys.argv[1]
native = sys.argv[2] if len(sys.argv) > 2 else os.path.join(here, "bin", "xtpdfworker.exe")
script = sys.argv[3] if len(sys.argv) > 3 else os.path.join(repo, "Tests", "GpuPdfium", "MuPdfViewportWorker.py")


class Worker:
    def __init__(self, cmd, env=None):
        self.p = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=env)

    def ask(self, request):
        self.p.stdin.write((json.dumps(request) + "\n").encode("utf-8"))
        self.p.stdin.flush()
        header = json.loads(self.p.stdout.readline())
        data = b""
        if "length" in header:
            data = self.p.stdout.read(header["length"])
        return header, data

    def close(self):
        try:
            self.p.stdin.close()
            self.p.wait(5)
        except Exception:
            self.p.kill()


def timed(worker, request):
    t = time.time()
    header, data = worker.ask(request)
    return header, data, time.time() - t


def diff(a, b):
    if len(a) != len(b):
        return f"length differs {len(a)} vs {len(b)}"
    if a == b:
        return "identical"
    total = 0
    biggest = 0
    different = 0
    for x, y in zip(a, b):
        if x != y:
            d = abs(x - y)
            total += d
            different += 1
            biggest = max(biggest, d)
    return f"{different} of {len(a)} bytes differ ({100.0 * different / len(a):.2f} %), mean {total / max(1, different):.1f}, max {biggest}"


python = sys.executable
env = dict(os.environ)
env["XTPDF_MUPDF_NO_RASTER_CACHE"] = "1"
workers = {"python": Worker([python, script], env), "native": Worker([native], env)}
base = {"path": pdf, "password": None, "memoryState": 0, "nativeListLimit": 4, "hidden": None, "displayWidth": 0}

meta = {}
for name, w in workers.items():
    header, data, seconds = timed(w, dict(base, op="metadata", page=0))
    meta[name] = header
    print(f"{name:7s} metadata: {header.get('count')} pages, {seconds:.2f}s" + (f"  ERROR {header['error']}" if "error" in header else ""))
if "sizes" in meta["python"] and "sizes" in meta["native"]:
    pairs = list(zip(meta["python"]["sizes"], meta["native"]["sizes"]))
    worst = max(max(abs(a[0] - b[0]), abs(a[1] - b[1])) for a, b in pairs) if pairs else 0
    print(f"page sizes: largest difference {worst:.3f} pt over {len(pairs)} pages")

count = meta["python"].get("count", 0)
pages = sorted({0, count // 2, count - 1}) if count else [0]
for page in pages:
    w, h = meta["python"]["sizes"][page]
    for label, req in (
        ("full width 1200", dict(base, page=page, fullWidth=1200, fullHeight=0, rect=[0, 0, 1200, 0], annotations=True, alpha=False)),
        ("crop of a 4000 px page", None),
    ):
        if req is None:
            fullw = 4000
            fullh = round(fullw * h / w)
            req = dict(base, page=page, fullWidth=fullw, fullHeight=fullh, rect=[1000, min(500, fullh // 3), 1200, 800], annotations=False, alpha=False, displayWidth=1000)
        results = {}
        for name, wk in workers.items():
            header, data, seconds = timed(wk, req)
            results[name] = (header, data, seconds)
        hp, dp, sp = results["python"]
        hn, dn, sn = results["native"]
        if "error" in hn or "error" in hp:
            print(f"page {page + 1} {label}: python {hp.get('error', 'ok')} | native {hn.get('error', 'ok')}")
            continue
        ink = lambda d: sum(1 for v in d[::7] if v < 200) * 7
        print(f"page {page + 1} {label} ({hp['width']}x{hp['height']}): python {sp:.2f}s, native {sn:.2f}s; {diff(dp, dn)}; dark bytes py {ink(dp)} native {ink(dn)}")

# Text operations: words, search and select must agree between the workers.
try:
    import pymupdf
    ref = pymupdf.open(pdf)
except Exception:
    ref = None
if ref is not None:
    for page in pages:
        pg = ref[page]
        words = pg.get_text("words")
        hw = {}
        for name, wk in workers.items():
            hw[name] = timed(wk, dict(base, op="words", page=page))
        rp, rn = hw["python"][0].get("rects", []), hw["native"][0].get("rects", [])
        worst = max((max(abs(a - b) for a, b in zip(x, y)) for x, y in zip(rp, rn)), default=0)
        print(f"page {page + 1} words: python {len(rp)} (pymupdf {len(words)}), native {len(rn)}, largest box difference {worst:.5f}; time {hw['python'][2]:.2f}s / {hw['native'][2]:.2f}s")
        if words:
            query = max((w[4] for w in words[:300]), key=len)
            data = dict(query=query, matchCase=False, wholeWord=False)
            sr = {name: timed(wk, dict(base, op="search", page=page, data=data)) for name, wk in workers.items()}
            mp, mn = sr["python"][0].get("matches", []), sr["native"][0].get("matches", [])
            same = len(mp) == len(mn) and all(a["snippet"] == b["snippet"] for a, b in zip(mp, mn))
            print(f"page {page + 1} search '{query}': python {len(mp)} matches, native {len(mn)}, snippets equal: {same}")
            # select from the middle of the first word to the middle of the 30th (display space -> user space)
            a = pymupdf.Point((words[0][0] + words[0][2]) / 2, (words[0][1] + words[0][3]) / 2) * ~pg.transformation_matrix
            w2 = words[min(30, len(words) - 1)]
            b = pymupdf.Point((w2[0] + w2[2]) / 2, (w2[1] + w2[3]) / 2) * ~pg.transformation_matrix
            sel = {name: timed(wk, dict(base, op="select", page=page, data=dict(ax=a.x, ay=a.y, bx=b.x, by=b.y))) for name, wk in workers.items()}
            tp, tn = sel["python"][0], sel["native"][0]
            print(f"page {page + 1} select: python {len(tp.get('text', ''))} chars / {len(tp.get('rects', []))} boxes, native {len(tn.get('text', ''))} / {len(tn.get('rects', []))}, text equal: {tp.get('text') == tn.get('text')}")
    ref.close()

# Layers: hide every second layer through the same request in both workers.
try:
    import pymupdf
    ocgs = list(pymupdf.open(pdf).get_ocgs().keys())
except Exception:
    ocgs = []
if ocgs:
    hidden = [f"{x} 0 R" for x in ocgs[::2]]
    print(f"layers: {len(ocgs)} OCGs, hiding {len(hidden)}")
    for page in pages:
        req = dict(base, page=page, fullWidth=1200, fullHeight=0, rect=[0, 0, 1200, 0], annotations=False, alpha=False, hidden=hidden)
        results = {name: timed(wk, req) for name, wk in workers.items()}
        (hp, dp, sp), (hn, dn, sn) = results["python"], results["native"]
        if "error" in hp or "error" in hn:
            print(f"page {page + 1} layers: python {hp.get('error', 'ok')} | native {hn.get('error', 'ok')}")
            continue
        plain = timed(workers["native"], dict(req, hidden=None))[1]
        print(f"page {page + 1} layers hidden: {diff(dp, dn)}; hidden vs visible differ in native: {plain != dn}")
else:
    print("layers: the file has no OCGs")

for w in workers.values():
    w.close()
