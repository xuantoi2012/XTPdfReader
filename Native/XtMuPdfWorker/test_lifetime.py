"""Block-stream/raster lifetime and repeated scrolling regression on temporary PDFs."""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import random
import subprocess
import tempfile
import threading

import pymupdf as fitz

HERE = Path(__file__).resolve().parent
MIB = 1024 * 1024


class MemoryCounters(ctypes.Structure):
    _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
        (name, ctypes.c_size_t) for name in ("PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
                                           "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage",
                                           "PagefileUsage", "PeakPagefileUsage", "PrivateUsage")]


def private_bytes(pid):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.restype = ctypes.c_void_p
    kernel.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel.OpenProcess(0x410, False, pid)
    assert handle
    try:
        counters = MemoryCounters()
        counters.cb = ctypes.sizeof(counters)
        psapi = ctypes.WinDLL("psapi")
        psapi.GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.POINTER(MemoryCounters), wintypes.DWORD]
        assert psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb)
        return counters.PrivateUsage
    finally:
        kernel.CloseHandle(handle)


def main():
    with tempfile.TemporaryDirectory(prefix="xt-native-life-") as folder:
        path = str(Path(folder) / "scroll.pdf")
        with fitz.open() as doc:
            rng = random.Random(17)
            for index in range(64):
                page = doc.new_page(width=256, height=256)
                image = fitz.Pixmap(fitz.csRGB, 256, 256, rng.randbytes(256 * 256 * 3), False)
                page.insert_image(page.rect, pixmap=image)
                page.insert_text((12, 30), f"Page {index + 1}")
            doc.save(path)
        env = dict(os.environ, XTPDF_FORCE_BLOCK_STREAM="1", XTPDF_BLOCK_KB="64",
                   XTPDF_BLOCK_BUDGET_MB="1", XTPDF_MUPDF_RASTER_MB="1", XTPDF_MUPDF_NO_RASTER_CACHE="0")
        with subprocess.Popen([str(HERE / "bin" / "xtpdfworker.exe")], stdin=subprocess.PIPE,
                              stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=env) as worker:
            deadline = threading.Timer(120, worker.kill)
            deadline.start()
            try:
                def ask(**request):
                    worker.stdin.write((json.dumps(request) + "\n").encode())
                    worker.stdin.flush()
                    line = worker.stdout.readline()
                    assert line, "worker stopped or timed out"
                    header = json.loads(line)
                    data = worker.stdout.read(header.get("length", 0))
                    assert len(data) == header.get("length", 0)
                    return header, data

                def render(page=0, memoryState=0):
                    reply = ask(path=path, page=page, fullWidth=256, fullHeight=0, rect=[0, 0, 256, 0],
                                annotations=False, nativeListLimit=2, memoryState=memoryState)
                    assert "error" not in reply[0], reply[0]
                    return reply

                original = render()[1]
                memory = []
                for sweep in range(4):
                    for page in range(64):
                        render(page)
                        stats = ask(op="stats", nativeListLimit=2)[0]
                        assert stats["rasterBytes"] <= MIB and stats["blockBytes"] <= MIB
                        assert stats["displayLists"] <= 2
                    memory.append(private_bytes(worker.pid))
                # Internal budgets are hard assertions. The allocator may retain freed pages,
                # but subsequent identical sweeps must not keep accumulating full raster sets.
                assert memory[-1] - memory[1] < 24 * MIB, memory
                ask(op="memory", memoryState=2, data=[dict(path=path, page=63)])
                stats = ask(op="stats", memoryState=2)[0]
                assert stats["rasterBytes"] == 0 and stats["blockBytes"] <= 64 * 1024
                assert render(memoryState=2)[1] == original, "pinned stream block corrupted after trim"

                # Changing size and timestamp must invalidate documents, lists, blocks and rasters.
                with fitz.open(path) as doc:
                    doc[0].draw_rect((0, 0, 200, 200), fill=(1, 0, 0))
                    doc.saveIncr()
                os.utime(path, None)
                assert render()[1] != original, "stale raster after file revision"
                ask(op="close", path=path)
                stats = ask(op="stats")[0]
                assert all(stats[k] == 0 for k in ("documents", "displayLists", "rasterBytes", "blockBytes"))
                # Bad requests return errors and leave the JSON/binary stream synchronized.
                assert "error" in ask(op="metadata", path=path, data=dict(first=-1, count=1))[0]
                assert "error" in render_error(ask, path)
                render()
                ask(op="release", data=[])
                assert ask(op="stats")[0]["documents"] == 0
                print("PASS: 256 page renders, bounded raster/list/block caches, pressure trim, file revision, close/release, protocol recovery")
                print("Private MiB after each 64-page sweep:", ", ".join(f"{n / MIB:.1f}" for n in memory))
            finally:
                deadline.cancel()
                worker.stdin.close()
                try:
                    worker.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    worker.kill()


def render_error(ask, path):
    return ask(path=path, page=999, fullWidth=256, fullHeight=0, rect=[0, 0, 256, 0])[0]


if __name__ == "__main__":
    main()
