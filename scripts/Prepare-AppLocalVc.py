"""Extract pinned Microsoft VC++ redistributables; never execute an installer.

UCRT files come from the Windows SDK redistributable directory, not System32.
The output contains a per-file manifest for packaging and loader verification.
"""
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts/vc-app-local'
SEVENZIP = Path(os.environ.get('ProgramFiles', r'C:\Program Files')) / '7-Zip/7z.exe'
SDK = Path(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)')) / 'Windows Kits/10/Redist/ucrt/DLLs'

def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def extract(source, target):
    target.mkdir(parents=True, exist_ok=True)
    subprocess.run([str(SEVENZIP), 'x', str(source), '-o' + str(target), '-y'], check=True, stdout=subprocess.DEVNULL)

def main():
    manifest = {'schema_version': 1, 'deployment': 'application-local', 'system_installation': False,
                'vc_version': '14.29.30157.0', 'ucrt_version': '10.0.10240.16384', 'files': {}, 'sources': []}
    sources = json.loads((ROOT / 'installer/ocr-prerequisites.json').read_text(encoding='utf-8'))
    for arch in ('x86', 'x64'):
        source = next(x for x in sources['files'] if x['file'].endswith(arch + '.exe'))
        installer = ROOT / 'artifacts/prerequisites' / source['file']
        assert sha(installer) == source['sha256']
        ps = "$s=Get-AuthenticodeSignature -LiteralPath '" + str(installer).replace("'", "''") + "';@{status=$s.Status.ToString();publisher=$s.SignerCertificate.Subject}|ConvertTo-Json -Compress"
        signature = json.loads(subprocess.check_output(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', ps], env={**os.environ, 'PSModulePath': os.environ['SystemRoot'] + r'\System32\WindowsPowerShell\v1.0\Modules'}).decode('utf-8-sig'))
        assert signature['status'] == 'Valid' and 'CN=Microsoft Corporation,' in signature['publisher']
        manifest['sources'].append({**source, 'signature': signature})
        work = OUT / 'extracted' / arch
        data = installer.read_bytes(); pos = 0; cabinets = []
        while True:
            pos = data.find(b'MSCF', pos)
            if pos < 0: break
            if pos + 36 <= len(data):
                size = struct.unpack_from('<I', data, pos + 8)[0]
                if 36 <= size <= len(data) - pos:
                    cab = work / ('container-' + str(len(cabinets)) + '.cab'); cab.parent.mkdir(parents=True, exist_ok=True)
                    cab.write_bytes(data[pos:pos + size]); folder = cab.with_suffix(''); extract(cab, folder); cabinets.append(folder)
            pos += 4
        assert len(cabinets) == 2, 'Expected Burn UX and attached payload cabinets'
        destination = OUT / arch; destination.mkdir(exist_ok=True)
        # Enumerate only DLL files extracted from the signed setup's native cabinets.
        for payload in cabinets[1].iterdir():
            if payload.is_file() and payload.read_bytes()[:4] == b'MSCF':
                expanded = payload.parent / (payload.name + '-files'); extract(payload, expanded)
                for dll in expanded.glob('*.dll'):
                    if dll.name.lower().startswith(('vcruntime140', 'msvcp140', 'concrt140')):
                        shutil.copy2(dll, destination / dll.name)
        for name in ('vcruntime140.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'concrt140.dll'):
            assert (destination / name).is_file(), name
        for dll in (SDK / arch).glob('*.dll'):
            # Include the SDK's complete app-local UCRT/API-forwarder set.
            shutil.copy2(dll, destination / dll.name)
        assert (destination / 'ucrtbase.dll').is_file()
        ux_manifest = ET.parse(cabinets[0] / '0')
        for node in ux_manifest.iter():
            if node.attrib.get('FilePath') == 'license.rtf':
                shutil.copy2(cabinets[0] / node.attrib['SourcePath'], OUT / 'MICROSOFT-VC-LICENSE.rtf')
        for dll in sorted(destination.glob('*.dll')):
            header = dll.read_bytes(); offset = struct.unpack_from('<I', header, 0x3c)[0]
            assert struct.unpack_from('<H', header, offset + 4)[0] == (0x14c if arch == 'x86' else 0x8664)
            manifest['files'][arch + '/' + dll.name] = sha(dll)
    (OUT / 'native-runtime.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print(json.dumps({'files': len(manifest['files']), 'system_installation': False, 'output': str(OUT)}))

if __name__ == '__main__': main()
