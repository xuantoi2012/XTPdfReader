import difflib
import json
import re
import sys
from pathlib import Path
import pymupdf

root = Path(sys.argv[1])
expected = json.loads((root/'word-expected.json').read_text(encoding='utf-8'))
doc = pymupdf.open(root/'word-120-headings.pdf')
actual = [{'title': title, 'level': level, 'page': page} for level, title, page in doc.get_toc()]
errors = []
ratios = []
unembedded = []
max_image_fraction = 0
for index, page in enumerate(doc):
    normalize = lambda s: re.sub(r'\s+', '', s)
    ratios.append(difflib.SequenceMatcher(None, normalize(expected['pages'][index]['text']), normalize(page.get_text())).ratio())
    if f'PAGE-{index+1:03d}' not in page.get_text():
        errors.append(f'Missing page marker {index+1}')
    for font in page.get_fonts():
        xref = font[0]
        name, ext, typ, data = doc.extract_font(xref)
        if not data:
            unembedded.append({'page':index+1, 'font':name, 'type':typ})
    for image in page.get_image_info():
        box = pymupdf.Rect(image['bbox'])
        max_image_fraction = max(max_image_fraction, box.get_area()/page.rect.get_area())
    if abs(page.rect.width-595.276)>1 or abs(page.rect.height-841.89)>1:
        errors.append(f'Unexpected page size {index+1}: {page.rect}')
result = {'pages':len(doc), 'expectedPages':120, 'bookmarks':len(actual), 'expectedBookmarks':len(expected['headings']),
          'exactHeadingTreeAndPages':actual==expected['headings'], 'minimumTextSimilarity':min(ratios),
          'unembeddedFonts':unembedded, 'maxImagePageFraction':max_image_fraction,
          'metadata':doc.metadata, 'errors':errors, 'generatorSHA256':expected['sha256'],
          'scope':'Native Word export only; no XT Reader queue, add-in, IPP transport or shelf delivery'}
(root/'word-validation.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
for index in [0,59,119]:
    doc[index].get_pixmap(matrix=pymupdf.Matrix(0.8,0.8)).save(root/f'word-120-page-{index+1}.png')
print(json.dumps(result, ensure_ascii=False, indent=2))
assert len(doc)==120 and actual==expected['headings'] and min(ratios)>=0.99 and not unembedded and not errors and max_image_fraction<0.5
