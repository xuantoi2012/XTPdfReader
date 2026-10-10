import hashlib, json, os, subprocess, sys, collections

exe, pdf, page, width = sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4])
runs = int(sys.argv[5]) if len(sys.argv) > 5 else 12
env = dict(os.environ, XTPDF_MUPDF_NO_RASTER_CACHE="1")
p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env=env)
hashes = collections.Counter()
for i in range(runs):
    req = dict(path=pdf, page=page - 1, fullWidth=width, fullHeight=0, rect=[0, 0, width, 0],
               alpha=False, annotations=True, displayWidth=1200)
    p.stdin.write((json.dumps(req) + "\n").encode()); p.stdin.flush()
    header = json.loads(p.stdout.readline())
    assert "error" not in header, header
    pixels = p.stdout.read(header["length"])
    hashes[hashlib.sha256(pixels).hexdigest()[:16]] += 1
p.stdin.close(); p.wait(timeout=10)
print(width, dict(hashes))
