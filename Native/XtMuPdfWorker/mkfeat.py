"""Build a PDF that exercises the graphics features band rendering must reproduce exactly."""
import math, zlib, sys

objs = []
def add(body):
    objs.append(body)
    return len(objs)

def stream(dict_body, data, compress=True):
    if compress:
        data = zlib.compress(data)
        dict_body += " /Filter /FlateDecode"
    return ("<< %s /Length %d >>\nstream\n" % (dict_body, len(data))).encode("latin1") + data + b"\nendstream"

W, H = 612, 792
# image 64x64 RGB gradient
img = bytearray()
for y in range(64):
    for x in range(64):
        img += bytes([x * 4 % 256, y * 4 % 256, (x ^ y) * 4 % 256])
image = add(stream("/Type /XObject /Subtype /Image /Width 64 /Height 64 /ColorSpace /DeviceRGB /BitsPerComponent 8", bytes(img)))

# transparency group xobject
grp_content = b"1 0 0 rg 0 0 120 120 re f 0 0 1 rg 60 60 120 120 re f 0 0.6 0 RG 8 w 10 10 m 170 170 l S"
group = add(stream("/Type /XObject /Subtype /Form /BBox [0 0 180 180] /Group << /S /Transparency /CS /DeviceRGB /I true /K false >>", grp_content))

# soft mask group (luminosity gradient via shading)
shade_axial = add("<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 200 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> /Extend [true true] >>")
mask_form = add(stream("/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Group << /S /Transparency /CS /DeviceGray >> /Resources << /Shading << /S1 %d 0 R >> >>" % shade_axial,
                       b"q 1 0 0 1 300 100 cm /S1 sh Q"))
gs_mask = add("<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G %d 0 R >> >>" % mask_form)
gs_alpha = add("<< /Type /ExtGState /ca 0.5 /CA 0.5 >>")
gs_mult = add("<< /Type /ExtGState /BM /Multiply /ca 0.8 >>")
gs_none = add("<< /Type /ExtGState /SMask /None /ca 1 /BM /Normal >>")

shade_radial = add("<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [100 100 0 100 100 90] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 1 0] /C1 [0.8 0 0.5] /N 1 >> /Extend [true true] >>")

# colored tiling pattern (dots + cross)
pat_content = b"0 0 0.8 rg 2 2 3 3 re f 1 0 0 RG 0.3 w 0 8 m 10 8 l S 8 0 m 8 10 l S"
pattern = add(stream("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >> /Matrix [1 0.2 -0.2 1 0 0]", pat_content))

font = add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>")

content = []
content.append(b"0.9 0.9 1 rg 0 0 612 792 re f")
# thin lines everywhere (hairlines, dashed)
for i in range(0, 80):
    content.append(("0 G 0 w %.2f 0 m %.2f 792 l S" % (i * 7.7, i * 7.7 + 40)).encode())
for i in range(0, 60):
    content.append(("0.2 0.2 0.8 RG 0.4 w [3 2 1 2] %d d 0 %.2f m 612 %.2f l S" % (i % 5, i * 13.1, i * 13.1 + 20)).encode())
content.append(b"[] 0 d")
# group with alpha
content.append(b"q /GSa gs 1 0 0 1 40 560 cm /Grp Do Q")
content.append(b"q /GSa gs 1 0 0 1 120 600 cm /Grp Do Q")
# multiply blend
content.append(b"q /GSm gs 0.9 0.5 0 rg 60 500 200 120 re f 0 0.5 0.9 rg 150 470 200 120 re f Q")
# soft mask with gradient fill
content.append(b"q /GSmask gs 0 0.5 0 rg 280 90 200 300 re f Q")
# radial shading clipped by a bezier path
content.append(b"q 1 0 0 1 350 420 cm 0 0 m 180 30 180 160 90 190 c 0 160 -40 80 0 0 c W n /SH2 sh Q")
# tiling pattern fill of a polygon
content.append(b"q /Pattern cs /P1 scn 40 150 m 300 120 l 330 330 l 160 380 l h f Q")
# clip by text and fill with stripes
content.append(b"q BT /F1 90 Tf 7 Tr 30 40 Td (CLIP) Tj ET")
for i in range(0, 40):
    content.append(("%.2f %.2f %.2f rg %d 40 6 130 re f" % ((i % 3) / 2, (i % 5) / 4, (i % 7) / 6, 30 + i * 7)).encode())
content.append(b"Q")
# stroke text + fill text
content.append(b"BT /F1 40 Tf 1 Tr 0.8 w 1 0 0 RG 40 720 Td (Stroke 123) Tj ET")
content.append(b"BT /F1 28 Tf 0 Tr 0 g 40 690 Td (Fill text near edges 0123456789) Tj ET")
# rotated, scaled image
content.append(b"q 200 90 -90 200 420 560 cm /Im1 Do Q")
content.append(b"q 120 0 0 60 40 350 cm /Im1 Do Q")
# even-odd star
pts = []
for k in range(5):
    a = k * 4 * math.pi / 5 - math.pi / 2
    pts.append((450 + 80 * math.cos(a), 330 + 80 * math.sin(a)))
content.append(("1 0.6 0 rg " + "%.2f %.2f m " % pts[0] + " ".join("%.2f %.2f l" % p for p in pts[1:]) + " h f*").encode())
content.append(b"1 w 0 0 0 RG 5 5 602 782 re S")
content_stream = add(stream("", b"\n".join(content)))

res = ("<< /ExtGState << /GSa %d 0 R /GSm %d 0 R /GSmask %d 0 R /GSnone %d 0 R >> /XObject << /Grp %d 0 R /Im1 %d 0 R >> "
       "/Shading << /SH2 %d 0 R >> /Pattern << /P1 %d 0 R >> /Font << /F1 %d 0 R >> >>"
       % (gs_alpha, gs_mult, gs_mask, gs_none, group, image, shade_radial, pattern, font))
page = add("<< /Type /Page /Parent %d 0 R /MediaBox [0 0 612 792] /Contents %d 0 R /Resources %s >>" % (len(objs) + 2, content_stream, res))
pages = add("<< /Type /Pages /Kids [%d 0 R] /Count 1 >>" % page)
catalog = add("<< /Type /Catalog /Pages %d 0 R >>" % pages)

out = bytearray(b"%PDF-1.7\n")
offsets = []
for i, o in enumerate(objs, 1):
    offsets.append(len(out))
    body = o if isinstance(o, bytes) else o.encode("latin1")
    out += ("%d 0 obj\n" % i).encode() + body + b"\nendobj\n"
xref = len(out)
out += ("xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)).encode()
for off in offsets:
    out += ("%010d 00000 n \n" % off).encode()
out += ("trailer\n<< /Size %d /Root %d 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, catalog, xref)).encode()
open(sys.argv[1], "wb").write(out)
print("wrote", sys.argv[1], len(out))
