# So PDFium và MuPDF trên cùng file (xem VIEC-TIEP-2026-09-28.md, mục "Vì sao không đổi sang MuPDF").
# Cài: pip install pymupdf pypdfium2
# Chạy: python engbench.py "D:\\duong\\dan\\file.pdf" 0,5,10,20,40   (số trang 0-based, cách nhau bằng dấu phẩy)
import time, sys, pymupdf, pypdfium2 as pdfium
path = sys.argv[1]; pages = [int(x) for x in sys.argv[2].split(',')]
def t(f):
    s=time.perf_counter(); r=f(); return r,(time.perf_counter()-s)*1000
FULLW=2304; ZOOMW=6600; RW,RH=2048,1152   # vùng xem ~ zoom 300%
res={'pdfium':[], 'mupdf':[], 'mupdf-dl':[]}
d=pdfium.PdfDocument(path)
m=pymupdf.open(path)
for i in pages:
    # PDFium
    p,load=t(lambda: d[i]); w,h=p.get_size()
    _,full=t(lambda: p.render(scale=FULLW/w))
    s=ZOOMW/w; cx,cy=w/2,h/2; rw,rh=RW/s,RH/s
    _,reg=t(lambda: p.render(scale=s, crop=(cx-rw/2, cy-rh/2, w-(cx+rw/2), h-(cy+rh/2))))
    res['pdfium'].append((load,full,reg)); p.close()
    # MuPDF trực tiếp
    q,load=t(lambda: m.load_page(i)); r=q.rect
    _,full=t(lambda: q.get_pixmap(matrix=pymupdf.Matrix(FULLW/r.width, FULLW/r.width)))
    s=ZOOMW/r.width; clip=pymupdf.Rect(r.width/2-RW/s/2, r.height/2-RH/s/2, r.width/2+RW/s/2, r.height/2+RH/s/2)
    _,reg=t(lambda: q.get_pixmap(matrix=pymupdf.Matrix(s,s), clip=clip))
    res['mupdf'].append((load,full,reg))
    # MuPDF display list: ghi 1 lần, vẽ lại vùng bất kỳ
    dl,rec=t(lambda: q.get_displaylist())
    _,full2=t(lambda: dl.get_pixmap(matrix=pymupdf.Matrix(FULLW/r.width, FULLW/r.width)))
    _,reg2=t(lambda: dl.get_pixmap(matrix=pymupdf.Matrix(s,s), clip=clip))
    res['mupdf-dl'].append((rec,full2,reg2))
for k,v in res.items():
    n=len(v); a=[sum(x[j] for x in v)/n for j in range(3)]; mx=[max(x[j] for x in v) for j in range(3)]
    lab=('ghi display list','vẽ cả trang (từ DL)','vùng zoom 300% (từ DL)') if k=='mupdf-dl' else ('mở/parse trang','vẽ cả trang 2304px','vùng zoom 300%')
    print(f"{k:9} " + " | ".join(f"{lab[j]} TB {a[j]:6.0f} max {mx[j]:6.0f} ms" for j in range(3)))
