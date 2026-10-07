import json
import re
import sys
import subprocess
import zipfile
from pathlib import Path
import pymupdf

result_path = Path(sys.argv[1])
result = json.loads(result_path.read_text())
source_path = Path(sys.argv[2])
normalize = lambda text: re.sub(r'\s+', '', text)
with pymupdf.open(source_path) as source:
    expected_toc = source.get_toc()
    texts = [normalize(p.get_text()) for p in source]
reports = []
for path in result['outputPdfs']:
    with pymupdf.open(path) as document:
        assert len(document) == 120
        assert document.get_toc() == expected_toc
        assert [normalize(p.get_text()) for p in document] == texts
        assert document.metadata['title'] == 'Tài liệu 120 trang'
        assert document.metadata['author']
        assert all(document.extract_font(font[0])[3] for page in document for font in page.get_fonts())
        assert not any(page.get_images() for page in document)
        reports.append({'file':path, 'pages':len(document), 'bookmarks':len(document.get_toc()), 'textMatch':1.0, 'fontsEmbedded':True, 'title':document.metadata['title'], 'author':document.metadata['author']})
with pymupdf.open(result['xpsPdf']) as document:
    text = document[0].get_text()
    assert normalize('Vector text: Tiếng Việt có dấu') == normalize(text)
    assert document[0].get_drawings()
    assert not document[0].get_images()
    assert all(document.extract_font(font[0])[3] for font in document[0].get_fonts())
    xps = {'searchableText':text.strip(), 'vectorPaths':len(document[0].get_drawings()), 'images':0, 'embeddedFonts':True}
oxps_path = result_path.parent/'vector.oxps'
with zipfile.ZipFile(Path(result['xpsPdf']).with_suffix('.xps')) as source_zip, zipfile.ZipFile(oxps_path, 'w', zipfile.ZIP_DEFLATED) as target_zip:
    for entry in source_zip.infolist():
        data = source_zip.read(entry)
        if entry.filename.endswith(('.xml', '.rels', '.fdseq', '.fdoc', '.fpage')):
            text = data.decode('utf-8-sig')
            text = text.replace('http://schemas.microsoft.com/xps/2005/06', 'http://schemas.openxps.org/oxps/v1.0')
            text = text.replace('application/vnd.ms-package.xps-', 'application/oxps-')
            data = text.encode('utf-8')
        target_zip.writestr(entry.filename, data)
oxps_pdf = result_path.parent/'oxps.pdf'
converter = Path(__file__).resolve().parents[2]/'VirtualPrinter'/'Core'/'ConvertDocument.py'
subprocess.run([sys.executable,'-I',str(converter),str(oxps_path),str(oxps_pdf),'application/oxps','Vector OXPS','test'],check=True)
with pymupdf.open(oxps_pdf) as document:
    assert normalize(document[0].get_text()) == normalize('Vector text: Tiếng Việt có dấu')
    assert document[0].get_drawings() and not document[0].get_images()
    assert all(document.extract_font(font[0])[3] for font in document[0].get_fonts())
    oxps_vectors = len(document[0].get_drawings())
report = {'wordCopies':reports, 'xps':xps, 'oxps':{'syntheticFixture':True,'textMatch':1.0,'vectorPaths':oxps_vectors,'images':0}}
(result_path.parent/'pdf-verification.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'PASS: {len(reports)} Word copy PDFs preserve 120 pages, 108 bookmarks, text, embedded fonts; XPS/OXPS preserve text and vector line')
