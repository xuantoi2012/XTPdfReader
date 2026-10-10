"""Native cancellation during preparation/rasterization, recovery and handle lifetime."""
import ctypes
from ctypes import wintypes
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import uuid

import pymupdf as fitz

HERE = Path(__file__).resolve().parent
kernel = ctypes.WinDLL("kernel32", use_last_error=True)
kernel.CreateEventW.argtypes = [ctypes.c_void_p, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
kernel.CreateEventW.restype = wintypes.HANDLE
kernel.SetEvent.argtypes = [wintypes.HANDLE]
kernel.CloseHandle.argtypes = [wintypes.HANDLE]
kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
kernel.OpenProcess.restype = wintypes.HANDLE
kernel.GetProcessHandleCount.argtypes = [wintypes.HANDLE, ctypes.POINTER(wintypes.DWORD)]


def handles(pid):
    handle = kernel.OpenProcess(0x1000, False, pid)
    assert handle
    try:
        count = wintypes.DWORD()
        assert kernel.GetProcessHandleCount(handle, ctypes.byref(count))
        return count.value
    finally:
        kernel.CloseHandle(handle)


def main():
    with tempfile.TemporaryDirectory(prefix="xt-cancel-") as folder:
        path = str(Path(folder) / "drawing.pdf")
        with fitz.open() as doc:
            page = doc.new_page(width=600, height=400)
            shape = page.new_shape()
            for i in range(5000):
                shape.draw_line((i % 600, 0), ((i * 17) % 600, 400))
            shape.finish(color=(.2, .3, .4), width=.1)
            shape.commit()
            doc.save(path)
        worker = subprocess.Popen([str(HERE / "bin" / "xtpdfworker.exe")], stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                  env=dict(os.environ, XTPDF_MUPDF_NO_RASTER_CACHE="1"))
        deadline = threading.Timer(90, worker.kill)
        deadline.start()

        def ask(request):
            worker.stdin.write((json.dumps(request) + "\n").encode())
            worker.stdin.flush()
            header = json.loads(worker.stdout.readline())
            pixels = worker.stdout.read(header.get("length", 0))
            assert len(pixels) == header.get("length", 0)
            return header, pixels

        small = dict(path=path, page=0, fullWidth=128, fullHeight=0, rect=[0, 0, 128, 0])
        large = dict(path=path, page=0, fullWidth=8192, fullHeight=0, rect=[0, 0, 8192, 0])

        def cancel(request, delay=None):
            name = "Local\\XTPdfCancel-" + uuid.uuid4().hex
            event = kernel.CreateEventW(None, True, delay is None, name)
            assert event
            timer = None
            try:
                if delay is not None:
                    timer = threading.Timer(delay, lambda: kernel.SetEvent(event))
                    timer.start()
                reply, pixels = ask(dict(request, cancelEvent=name))
                assert reply.get("error") == "render cancelled" and not pixels, reply
            finally:
                if timer:
                    timer.cancel()
                    timer.join()
                kernel.CloseHandle(event)

        try:
            expected = ask(small)[1]
            for _ in range(3):
                cancel(large)
            before = handles(worker.pid)
            for _ in range(32):
                cancel(large)
            after = handles(worker.pid)
            assert after <= before + 8, ("cancel handle leak", before, after)
            cancel(large, .02)  # an active raster, with its complete display list already cached
            stats = ask(dict(op="stats"))[0]
            assert stats["displayLists"] == 1 and stats["sharedBytes"] == 0, stats
            assert ask(small)[1] == expected, "cancelled raster corrupted a cached display list"
            ask(dict(op="close", path=path))
            cancel(large)  # a request cancelled before preparation creates no list
            assert ask(dict(op="stats"))[0]["displayLists"] == 0
            cancel(large, .001)  # may stop preparation or the first raster; no partial list may survive
            assert ask(small)[1] == expected, "cancelled preparation poisoned the next render"
            assert ask(dict(op="stats"))[0]["cancelledRenders"] == 38
            print(f"PASS: 38 cancelled frames, no partial pixels/lists, same-worker recovery, handles {before} -> {after}")
        finally:
            deadline.cancel()
            worker.stdin.close()
            try:
                worker.wait(timeout=5)
            except subprocess.TimeoutExpired:
                worker.kill()
                worker.wait()
            worker.stdout.close()


if __name__ == "__main__":
    main()
