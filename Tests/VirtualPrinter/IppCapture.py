"""Disposable phase-0 loopback capture endpoint. Not a production print service."""
import argparse
import json
import struct
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--port', type=int, default=18631)
parser.add_argument('--raster', action='store_true', help='Also advertise PWG raster for a separate negotiation test')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=True)
uri = f'ipp://127.0.0.1:{args.port}/ipp/print'

def attr(tag, name, value):
    if isinstance(value, int):
        value = struct.pack('>i', value)
    elif isinstance(value, str):
        value = value.encode()
    return bytes([tag])+struct.pack('>H', len(name))+name.encode()+struct.pack('>H', len(value))+value

def values(tag, name, items):
    return b''.join(attr(tag, name if index == 0 else '', value) for index, value in enumerate(items))

class Capture(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.client_address[0] != '127.0.0.1':
            self.send_error(403)
            return
        if self.headers.get('Transfer-Encoding','').lower() == 'chunked':
            chunks = []
            total = 0
            while True:
                size = int(self.rfile.readline().strip().split(b';')[0],16)
                if not size:
                    self.rfile.readline()
                    break
                total += size
                if total > 64*1024*1024:
                    self.send_error(413)
                    return
                chunks.append(self.rfile.read(size))
                self.rfile.read(2)
            body = b''.join(chunks)
        else:
            size = int(self.headers.get('Content-Length',0))
            if size > 64*1024*1024:
                self.send_error(413)
                return
            body = self.rfile.read(size)
        if len(body)<8:
            self.send_error(400)
            return
        op = int.from_bytes(body[2:4],'big')
        pos, attributes, name = 8, {}, ''
        while pos < len(body):
            tag = body[pos]
            pos += 1
            if tag == 3:
                break
            if tag < 0x10:
                continue
            length = int.from_bytes(body[pos:pos+2],'big'); pos += 2
            if length:
                name = body[pos:pos+length].decode(errors='replace')
            pos += length
            length = int.from_bytes(body[pos:pos+2],'big'); pos += 2
            value = body[pos:pos+length]; pos += length
            attributes.setdefault(name,[]).append(value.decode(errors='replace') if tag>=0x40 else value.hex())
        payload = body[pos:]
        record = {'operation':op, 'attributes':attributes, 'documentBytes':len(payload), 'magicHex':payload[:32].hex()}
        with (args.output/'ipp-requests.jsonl').open('a',encoding='utf-8') as log:
            log.write(json.dumps(record)+'\n')
        if payload:
            (args.output/f'job-{time.time_ns()}.bin').write_bytes(payload)
        response = body[:2]+b'\x00\x00'+body[4:8]+b'\x01'+attr(0x47,'attributes-charset','utf-8')+attr(0x48,'attributes-natural-language','en')
        if op == 11:
            formats = ['application/pdf','application/oxps','application/vnd.ms-xpsdocument']
            if args.raster:
                formats.append('image/pwg-raster')
            response += b'\x04'+attr(0x45,'printer-uri-supported',uri)+attr(0x44,'uri-authentication-supported','none')+attr(0x44,'uri-security-supported','none')
            response += attr(0x42,'printer-name','XT Phase0 Probe')+attr(0x41,'printer-make-and-model','XT Phase0 Capture')+attr(0x42,'printer-info','Disposable XT vector negotiation probe')
            response += attr(0x23,'printer-state',3)+attr(0x44,'printer-state-reasons','none')+attr(0x22,'printer-is-accepting-jobs',b'\x01')+attr(0x21,'queued-job-count',0)
            response += values(0x44,'ipp-versions-supported',['1.1','2.0'])+values(0x23,'operations-supported',[2,4,5,6,8,9,10,11])
            response += values(0x49,'document-format-supported',formats)+attr(0x49,'document-format-default','application/pdf')
            response += values(0x44,'media-supported',['iso_a4_210x297mm','na_letter_8.5x11in'])+attr(0x44,'media-default','iso_a4_210x297mm')
            response += attr(0x44,'sides-supported','one-sided')+attr(0x44,'sides-default','one-sided')+attr(0x22,'color-supported',b'\x01')
            response += values(0x44,'print-color-mode-supported',['color','monochrome'])+attr(0x44,'print-color-mode-default','color')
            response += attr(0x32,'printer-resolution-supported',struct.pack('>iiB',600,600,3))+attr(0x32,'printer-resolution-default',struct.pack('>iiB',600,600,3))
        elif op in (2,5,6,9):
            response += b'\x02'+attr(0x21,'job-id',1)+attr(0x45,'job-uri',uri+'/jobs/1')+attr(0x23,'job-state',9)+attr(0x44,'job-state-reasons','job-completed-successfully')
        response += b'\x03'
        self.send_response(200)
        self.send_header('Content-Type','application/ipp')
        self.send_header('Content-Length',str(len(response)))
        self.end_headers()
        self.wfile.write(response)

print(f'Listening on {uri}; raster advertised={args.raster}', flush=True)
ThreadingHTTPServer(('127.0.0.1',args.port), Capture).serve_forever()
