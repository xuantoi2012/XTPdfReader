"""Verify missing Windows fonts on a real PDF without writing to the PDF."""
import hashlib
from pathlib import Path
import sys
import pymupdf as fitz

source = Path(sys.argv[1])
output = Path(sys.argv[2])
output.mkdir(parents=True, exist_ok=True)
worker = Path(__file__).with_name("MuPdfViewportWorker.py")
# Load just the worker helpers; do not start its stdin request loop.
namespace = {}
exec(compile(worker.read_text(encoding="utf-8-sig").split("documents = OrderedDict()")[0], str(worker), "exec"), namespace)
resolve = namespace["resolve_unembedded_fonts"]
def digest():
    with source.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()
before = digest()
changes = 0
with fitz.open(source) as original, fitz.open(source) as corrected:
    for number in range(min(3, len(original))):
        page = corrected[number]
        resolve(corrected, page)
        old = original[number].get_pixmap(matrix=fitz.Matrix(1.3, 1.3))
        new = page.get_pixmap(matrix=fitz.Matrix(1.3, 1.3))
        assert (old.width, old.height) == (new.width, new.height)
        changes += old.samples != new.samples
        # Existing Vietnamese text encodings must remain usable after fallback.
        assert original[number].get_text() == page.get_text()
        new.save(output / f"font-corrected-{number+1}.png")
    streams = getattr(corrected, "_xt_system_font_streams", {})
    assert streams and changes, "Expected unembedded fonts to resolve to installed Windows fonts"
    print("Font files used:", ", ".join(str(path.name) for path in streams))
assert digest() == before, "Source PDF must remain unchanged"
print("PASS: page dimensions and Unicode text preserved, rendered fonts corrected, source SHA-256 unchanged")
print("SHA-256:", before)
