"""One native process, shared list, one/two native rendering threads."""
import argparse
import hashlib
import json
from pathlib import Path
import statistics
import subprocess
import time
import psutil
import pymupdf as fitz

p = argparse.ArgumentParser()
p.add_argument("source")
p.add_argument("worker")
p.add_argument("--output", required=True)
a = p.parse_args()
out = Path(a.output)
out.mkdir(parents=True, exist_ok=True)
reference = {}
for threads in [1, 2]:
    process = subprocess.Popen([a.worker, str(threads)], stdin=subprocess.PIPE,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        for fullw in [3000, 6000, 12000]:
            fullh = round(fullw * 842 / 1191)
            rect = [(fullw-1920)//2, (fullh-1080)//2, 1920, 1080]
            times = []
            for run in range(5):
                start = time.perf_counter()
                process.stdin.write((json.dumps(dict(path=a.source, page=0,
                    fullWidth=fullw, fullHeight=fullh, rect=rect)) + "\n").encode())
                process.stdin.flush()
                line = process.stdout.readline()
                if not line:
                    raise RuntimeError(process.stderr.read().decode(errors="replace"))
                header = json.loads(line)
                if "error" in header:
                    raise RuntimeError(header["error"])
                raw = process.stdout.read(header["length"])
                if len(raw) != 1920*1080*3:
                    raise RuntimeError("Incomplete bitmap")
                elapsed = (time.perf_counter()-start)*1000
                if threads == 1:
                    reference[fullw] = raw
                diff = sum(x!=y for x,y in zip(raw,reference[fullw])) if raw != reference[fullw] else 0
                delta = max(abs(x-y) for x,y in zip(raw,reference[fullw])) if diff else 0
                if run > 0:
                    times.append(elapsed)
                if run == 0:
                    fitz.Pixmap(fitz.csRGB, 1920, 1080, raw, False).save(str(out/f"native-{threads}-{fullw}.png"))
                print(json.dumps(dict(threads=threads, stage="render", full_width=fullw, run=run,
                    total_ms=elapsed, private_mib=psutil.Process(process.pid).memory_info().private/1048576,
                    differing_bytes=diff, max_channel_difference=delta,
                    sha256=hashlib.sha256(raw).hexdigest(), header=header)), flush=True)
            print(json.dumps(dict(threads=threads, stage="summary", full_width=fullw,
                median_ms=statistics.median(times))), flush=True)
    finally:
        process.kill()
        process.wait()
