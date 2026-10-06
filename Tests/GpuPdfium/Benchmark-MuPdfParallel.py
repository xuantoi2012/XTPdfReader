"""Compare isolated MuPDF workers; never share PyMuPDF objects across threads."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import time
import psutil

p = argparse.ArgumentParser()
p.add_argument("source")
a = p.parse_args()
worker_script = Path(__file__).with_name("MuPdfViewportWorker.py")
reference = {}

class Worker:
    def __init__(self):
        self.process = subprocess.Popen([sys.executable, "-u", str(worker_script)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            creationflags=subprocess.CREATE_NO_WINDOW)

    def render(self, fullw, fullh, rect):
        req = dict(path=a.source, page=0, fullWidth=fullw, fullHeight=fullh, rect=rect)
        self.process.stdin.write((json.dumps(req) + "\n").encode())
        self.process.stdin.flush()
        line = self.process.stdout.readline()
        if not line:
            raise RuntimeError("Worker exited before response")
        header = json.loads(line)
        if "error" in header:
            raise RuntimeError(header["error"])
        raw = self.process.stdout.read(header["length"])
        if len(raw) != header["length"]:
            raise RuntimeError("Truncated worker response")
        return header, raw

    def close(self):
        self.process.kill()
        self.process.wait()

def rectangles(target, count, fullh):
    x, y, w, h = target
    pieces = []
    for i in range(count):
        top, bottom = y + h * i // count, y + h * (i + 1) // count
        padded_top, padded_bottom = max(0, top - 2), min(fullh, bottom + 2)
        pieces.append(([x, padded_top, w, padded_bottom - padded_top], top, bottom))
    return pieces

for count in [1, 2, 4]:
    workers = []
    try:
        workers = [Worker() for _ in range(count)]
        with ThreadPoolExecutor(max_workers=count) as executor:
            started = time.perf_counter()
            # Warm each process's own list, not a shared Python display list.
            futures = [executor.submit(w.render, 6000, 4242, [2040, 1581, 64, 64]) for w in workers]
            for future in futures:
                future.result(timeout=30)
            print(json.dumps(dict(workers=count, stage="prepare", ms=(time.perf_counter()-started)*1000)), flush=True)
            for fullw in [3000, 6000, 12000]:
                fullh = round(fullw * 842 / 1191)
                target = [(fullw - 1920)//2, (fullh - 1080)//2, 1920, 1080]
                pieces = rectangles(target, count, fullh)
                durations = []
                for run in range(5):
                    started = time.perf_counter()
                    futures = [executor.submit(w.render, fullw, fullh, piece[0])
                               for w, piece in zip(workers, pieces)]
                    results = [f.result(timeout=30) for f in futures]
                    composite = bytearray(1920 * 1080 * 3)
                    for (rect, top, bottom), (header, raw) in zip(pieces, results):
                        if (header["width"], header["height"]) != (rect[2], rect[3]):
                            raise RuntimeError("Unexpected rounded crop dimensions")
                        src = (top - rect[1]) * header["stride"]
                        dst = (top - target[1]) * 1920 * 3
                        size = (bottom - top) * 1920 * 3
                        composite[dst:dst+size] = raw[src:src+size]
                    elapsed = (time.perf_counter() - started)*1000
                    digest = hashlib.sha256(composite).hexdigest()
                    if count == 1:
                        reference[fullw] = bytes(composite)
                    baseline = reference[fullw]
                    diff = sum(x != y for x, y in zip(baseline, composite)) if bytes(composite) != baseline else 0
                    maxdiff = max((abs(x-y) for x,y in zip(baseline,composite)), default=0) if diff else 0
                    if run > 0:
                        durations.append(elapsed)
                    memory = sum(psutil.Process(w.process.pid).memory_info().private for w in workers)/1048576
                    print(json.dumps(dict(workers=count, stage="render", full_width=fullw, run=run,
                        total_ms=elapsed, workers_private_mib=memory, differing_bytes=diff,
                        max_channel_difference=maxdiff, sha256=digest)), flush=True)
                print(json.dumps(dict(workers=count, stage="summary", full_width=fullw,
                    median_ms=statistics.median(durations), min_ms=min(durations), max_ms=max(durations))), flush=True)
    finally:
        for worker in workers:
            worker.close()
