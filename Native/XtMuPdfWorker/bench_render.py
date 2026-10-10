"""Compare two worker binaries, bypassing raster caches. Source PDFs are read only."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import subprocess
import threading
import time


def percentile(values, q):
    return sorted(values)[max(0, math.ceil(len(values) * q) - 1)]


def measure(executable, path, page, repeats):
    env = dict(os.environ, XTPDF_MUPDF_NO_RASTER_CACHE="1")
    process = subprocess.Popen([executable], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=subprocess.DEVNULL, env=env)
    deadline = threading.Timer(180, process.kill)
    deadline.start()
    results = {}
    try:
        cases = [("full-1200", 1200, 0, [0, 0, 1200, 0], False),
                 ("full-4608", 4608, 0, [0, 0, 4608, 0], False),
                 ("full-8192", 8192, 0, [0, 0, 8192, 0], False),
                 ("crop", 6001, 4249, [133, 191, 1201, 801], False),
                 ("alpha-crop", 6001, 4249, [133, 191, 1201, 801], True)]
        for name, width, height, rect, alpha in cases:
            wall, raster = [], []
            digest = None
            for iteration in range(repeats + 2):
                request = dict(path=path, page=page-1, fullWidth=width, fullHeight=height,
                               rect=rect, alpha=alpha, annotations=True, displayWidth=1200)
                start = time.perf_counter()
                process.stdin.write((json.dumps(request) + "\n").encode())
                process.stdin.flush()
                header = json.loads(process.stdout.readline())
                assert "error" not in header, header
                pixels = process.stdout.read(header["length"])
                elapsed = (time.perf_counter() - start) * 1000
                assert len(pixels) == header["length"]
                actual = hashlib.sha256(pixels).hexdigest()
                assert digest is None or actual == digest, f"unstable pixels: {name}"
                digest = actual
                if iteration >= 2:
                    wall.append(elapsed)
                    raster.append(header["renderMs"])
            results[name] = dict(bytes=header["length"], sha256=digest, samples=len(wall),
                                 wallP50=statistics.median(wall), wallP95=percentile(wall, .95),
                                 rasterP50=statistics.median(raster), rasterP95=percentile(raster, .95))
    finally:
        deadline.cancel()
        process.stdin.close()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
        process.stdout.close()
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("pdf")
    parser.add_argument("--baseline", required=True)
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--page", type=int, default=1)
    parser.add_argument("--repeats", type=int, default=12)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    assert args.repeats >= 3
    path = str(Path(args.pdf).resolve())
    before = os.stat(path)
    baseline = measure(str(Path(args.baseline).resolve()), path, args.page, args.repeats)
    candidate = measure(str(Path(args.candidate).resolve()), path, args.page, args.repeats)
    for name, prior in baseline.items():
        after = candidate[name]
        assert prior["sha256"] == after["sha256"], f"pixel regression: {name}"
        print(f"{name}: raster p50 {prior['rasterP50']:.2f} -> {after['rasterP50']:.2f} ms; "
              f"end-to-end p50 {prior['wallP50']:.2f} -> {after['wallP50']:.2f} ms; identical pixels", flush=True)
    source_after = os.stat(path)
    assert (before.st_size, before.st_mtime_ns) == (source_after.st_size, source_after.st_mtime_ns)
    Path(args.output).write_text(json.dumps(dict(pdf=path, page=args.page, baseline=baseline,
                                                candidate=candidate), indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
