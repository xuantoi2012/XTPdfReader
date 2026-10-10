"""Compare native OCR with Python on the Vietnamese scan built by TestOcrEndToEnd.
Usage: python test_ocr.py path/to/results/ocr/scan.pdf
"""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent


def run(command, job, folder):
    jobfile = Path(folder) / "ocr-job.json"
    jobfile.write_text(json.dumps(job), encoding="utf-8")
    result = subprocess.run(command + [str(jobfile)], capture_output=True, timeout=120,
                            env=dict(os.environ, PYTHONIOENCODING="utf-8"))
    messages = [json.loads(line) for line in result.stdout.decode("utf-8").splitlines() if line.startswith("{")]
    assert result.returncode == 0, (messages, result.stderr.decode(errors="replace"))
    assert messages[-1]["type"] == "done", messages
    return messages


def main():
    pdf = str(Path(sys.argv[1]).resolve())
    native = [str(HERE / "bin" / "xtpdfworker.exe"), "--ocr"]
    python = [sys.executable, str(REPO / "Services" / "Ocr" / "OcrWorker.py")]
    base = dict(path=pdf, tessdata=str(REPO / "Services" / "Ocr" / "tessdata"), language="vie", dpi=300)
    scenarios = [base, dict(base, pages=[0, 2], tile=1000, overlap=160),
                 dict(base, pages=[0, 999, 1]),
                 dict(base, mode="regions", requests=[dict(page=p, key="title", rect=[0.03, 0.04, 0.97, 0.2]) for p in range(4)] +
                      [dict(page=0, key="empty", rect=[0.9, 0.9, 0.95, 0.95])])]
    with tempfile.TemporaryDirectory(prefix="xt-ocr-ab-") as folder:
        for index, job in enumerate(scenarios):
            expected = run(python, job, folder)
            actual = run(native, job, folder)
            assert len(actual) == len(expected), (index, actual)
            worst = 0.0
            for a, b in zip(actual, expected):
                assert a["type"] == b["type"], (a, b)
                if a["type"] == "page":
                    assert a["page"] == b["page"] and a["skipped"] == b["skipped"]
                    assert [w[0] for w in a["words"]] == [w[0] for w in b["words"]], (index, a, b)
                    worst = max(worst, max((abs(x - y) for w, v in zip(a["words"], b["words"]) for x, y in zip(w[1:], v[1:])), default=0))
                elif a["type"] == "regions":
                    assert a["texts"] == b["texts"], (index, a, b)
                elif a["type"] == "error":
                    assert a["page"] == b["page"] == 999
            assert worst < 0.002, (index, worst)
            print(f"PASS OCR scenario {index + 1}: equal texts, largest normalized box difference {worst:.6f}")


if __name__ == "__main__":
    main()
