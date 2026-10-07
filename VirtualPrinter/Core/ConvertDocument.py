"""Use the Reader's existing MuPDF runtime; do not render pages into images."""
import os
import sys
import pymupdf

source, target, mime, title, author = sys.argv[1:]
with pymupdf.open(source, filetype='pdf' if mime == 'application/pdf' else 'xps') as document:
    if document.needs_pass or document.page_count == 0 or document.page_count > 10000:
        raise ValueError('Encrypted, empty or oversized document')
    if mime == 'application/pdf':
        pdf = document
    else:
        pdf = pymupdf.open('pdf', document.convert_to_pdf())
    metadata = pdf.metadata
    metadata.update(title=title, author=author)
    pdf.set_metadata(metadata)
    pdf.save(target, garbage=0, deflate=True)
    if pdf is not document:
        pdf.close()
with pymupdf.open(target) as check:
    if check.page_count == 0:
        raise ValueError('Empty conversion')
with open(target, 'rb') as result:
    result.seek(max(0, os.path.getsize(target)-1024))
    if b'%%EOF' not in result.read():
        raise ValueError('Incomplete PDF')
