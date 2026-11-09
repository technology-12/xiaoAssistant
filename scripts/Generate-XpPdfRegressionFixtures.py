"""Deterministic PDF xref/update fixtures; pypdf is an independent reference reader."""
from pathlib import Path
import json,re,struct,zlib
from pypdf import PdfReader,PdfWriter

ROOT=Path(__file__).resolve().parents[1]
OUT=ROOT/'tests/fixtures/xp-pdf'
OUT.mkdir(parents=True,exist_ok=True)
def stream(text):
    data=text if isinstance(text,bytes) else text.encode('ascii')
    return b'<< /Length '+str(len(data)).encode()+b' >>\nstream\n'+data+b'\nendstream'
def text(value):return stream('BT /F1 12 Tf 10 700 Td ('+value+') Tj ET')
def page(ref):return ('<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 800] /Resources << /Font << /F1 4 0 R >> >> /Contents '+ref+' >>').encode()
objects={1:b'<< /Type /Catalog /Pages 2 0 R >>',2:b'<< /Type /Pages /Count 1 /Kids [3 0 R] >>',3:page('5 0 R'),4:b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',5:text('OLD')}
def add_object(data,n,g,body):
    at=len(data);data+=f'{n} {g} obj\n'.encode()+body+b'\nendobj\n';return data,at
def append(data,updates,free=(),root='1 0 R',info=None):
    previous=int(re.findall(rb'startxref\s+(\d+)',data)[-1]) if b'startxref' in data else None
    entries={n:(0,g,'f') for n,g in free}
    for n,g,body in updates:
        data,at=add_object(data,n,g,body);entries[n]=(at,g,'n')
    at=len(data);data+=b'xref\n'
    for n,(offset,g,kind) in sorted(entries.items()):data+=f'{n} 1\n{offset:010d} {g:05d} {kind} \n'.encode()
    size=max([n for n in entries]+[20])+1
    trailer=f'<< /Size {size} /Root {root}'+(' /Prev '+str(previous) if previous is not None else '')+(' /Info '+info if info else '')+' >>'
    return data+b'trailer\n'+trailer.encode()+f'\nstartxref\n{at}\n%%EOF\n'.encode()
base=append(b'%PDF-1.7\n',[(n,0,b) for n,b in objects.items()],free=[(0,65535)])
fixtures={
 'classic-content-update.pdf':(append(base,[(5,0,text('LATEST'))]),'LATEST'),
 'generation-reuse.pdf':(append(base,[(3,0,page('5 1 R')),(5,1,text('REUSED'))]),'REUSED'),
 'metadata-update.pdf':(append(base,[(10,0,b'<< /Title (literal 5 0 R is text) >>')],info='10 0 R'),'OLD'),
 'freed-content.pdf':(append(base,[(3,0,page('8 0 R')),(8,0,text('AFTER_FREE'))],free=[(5,1)]),'AFTER_FREE'),
 'freed-reference-invalid.pdf':(append(base,[],free=[(5,1)]),None),
}
# An object physically appended but absent from the current xref must not supersede the live object.
orphan,_=add_object(base,5,0,text('UNREFERENCED'))
fixtures['orphan-object.pdf']=(append(orphan,[(10,0,b'<< /Title (xref wins) >>')],info='10 0 R'),'OLD')
temp=OUT/'base.pdf';temp.write_bytes(base)
w=PdfWriter(PdfReader(temp),incremental=True);w.add_metadata({'/Title':'stream xref update'})
with (OUT/'xref-stream-update.pdf').open('wb') as f:w.write(f)
fixtures['xref-stream-update.pdf']=((OUT/'xref-stream-update.pdf').read_bytes(),'OLD')
# A compressed object stream, xref PNG-Up predictor, then a classic incremental update.
packed=[(n,objects[n]) for n in (1,2,3,4)];body=b'';headers=[]
for n,value in packed:headers.append(f'{n} {len(body)}');body+=value+b' '
header=(' '.join(headers)+' ').encode();compressed=zlib.compress(header+body)
container=b'<< /Type /ObjStm /N 4 /First '+str(len(header)).encode()+b' /Length '+str(len(compressed)).encode()+b' /Filter /FlateDecode >>\nstream\n'+compressed+b'\nendstream'
data=b'%PDF-1.7\n';data,content_at=add_object(data,5,0,text('COMPRESSED'));data,container_at=add_object(data,6,0,container);xref_at=len(data)
rows=[struct.pack('>BIH',0,0,65535)]+[struct.pack('>BIH',2,6,i) for i in range(4)]+[struct.pack('>BIH',1,content_at,0),struct.pack('>BIH',1,container_at,0),struct.pack('>BIH',1,xref_at,0)]
previous=bytes(7);predicted=b''
for row in rows:predicted+=b'\x02'+bytes((v-previous[i])%256 for i,v in enumerate(row));previous=row
encoded=zlib.compress(predicted)
xref=b'<< /Type /XRef /Size 8 /Root 1 0 R /W [1 4 2] /Filter /FlateDecode /DecodeParms << /Predictor 12 /Columns 7 >> /Length '+str(len(encoded)).encode()+b' >>\nstream\n'+encoded+b'\nendstream'
data,_=add_object(data,7,0,xref);data+=f'startxref\n{xref_at}\n%%EOF\n'.encode()
fixtures['object-stream.pdf']=(data,'COMPRESSED')
fixtures['object-stream-incremental.pdf']=(append(data,[(5,0,text('COMPRESSED_LATEST'))]),'COMPRESSED_LATEST')
expected=[]
for name,(data,value) in fixtures.items():
    path=OUT/name;path.write_bytes(data)
    if value is not None:
        actual=''.join(PdfReader(path).pages[0].extract_text().split());assert actual==value,(name,actual,value)
    expected.append({'file':name,'text':value,'error':'UNSUPPORTED_FORMAT' if value is None else None})
(OUT/'expected.json').write_text(json.dumps(expected,indent=2),encoding='utf-8')
print(f'{len(fixtures)} PDF fixtures generated; valid files independently checked with pypdf.')
