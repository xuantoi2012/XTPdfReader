"""Summarize xperf PerfInfo-provider CSV without exposing other processes."""
import argparse
import collections
import csv
import json

parser = argparse.ArgumentParser()
parser.add_argument("csv_path")
parser.add_argument("--pid", type=int, required=True)
args = parser.parse_args()
threads = collections.Counter()
modules = collections.Counter()
seconds = collections.defaultdict(collections.Counter)
with open(args.csv_path, encoding="utf-8-sig", errors="replace", newline="") as source:
    for row in csv.reader(source):
        if len(row) < 9 or row[0].strip() != "SampledProfile":
            continue
        if not row[2].strip().endswith(f"({args.pid})"):
            continue
        try:
            second = int(row[1]) // 1_000_000
            tid = int(row[3])
            count = int(row[8])
        except ValueError:
            continue
        threads[tid] += count
        modules[row[7].strip().split("!")[0]] += count
        seconds[second][tid] += count
print(json.dumps({
    "pid": args.pid,
    "sample_count": sum(threads.values()),
    "threads": threads.most_common(),
    "modules": modules.most_common(),
    "per_second": {k: dict(v) for k, v in sorted(seconds.items())},
    "note": "Samples are not wall-clock latency. Same-second activity does not prove simultaneous execution."
}, indent=2))
