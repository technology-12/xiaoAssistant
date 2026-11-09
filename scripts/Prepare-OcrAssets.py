"""Restore pinned OCR models; no system installation or model API calls."""
import hashlib
import json
from pathlib import Path
import urllib.request

ROOT = Path(__file__).resolve().parents[1]


def main():
    manifest = json.loads((ROOT / 'assets/ocr/sources.json').read_text(encoding='utf-8'))
    for item in manifest['files']:
        target = ROOT / 'assets/ocr/tessdata' / item['file']
        if target.exists() and hashlib.sha256(target.read_bytes()).hexdigest() == item['sha256']:
            continue
        data = urllib.request.urlopen(item['url'], timeout=120).read()
        if len(data) != item['bytes'] or hashlib.sha256(data).hexdigest() != item['sha256']:
            raise RuntimeError('OCR asset fingerprint mismatch: ' + item['file'])
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
    print('OCR assets verified against pinned source fingerprints.')


if __name__ == '__main__':
    main()
