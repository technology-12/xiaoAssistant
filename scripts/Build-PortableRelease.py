"""Package the verified usability release; collect only explicit application payloads."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NAME = 'XiaoAssistant-1.0.1-portable'
CANDIDATE = ROOT / 'artifacts/usability-candidate'
PACKAGES = ROOT / 'artifacts/packages'
OUTPUT = PACKAGES / NAME
MAIN_EXE = '晓助手.exe'
INSTALLER = ROOT / 'artifacts/prerequisites/NDP48-x86-x64-AllOS-ENU.exe'
INSTALLER_SHA = '0a3a390c47e639d0f7fc65b21195fee6b7f65b066f80f70c60fab191d14b7e40'


def sha(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def text(path, value, encoding='utf-8'):
    path.write_bytes(value.replace('\r\n', '\n').replace('\n', '\r\n').encode(encoding))


def main():
    archive_path = PACKAGES / (NAME + '.zip')
    if OUTPUT.exists() or archive_path.exists():
        raise SystemExit('Release already exists; preserve it and use a new version name.')
    assert sha(INSTALLER) == INSTALLER_SHA
    command = "$s=Get-AuthenticodeSignature -LiteralPath '" + str(INSTALLER).replace("'", "''") + "';@{status=$s.Status.ToString();publisher=$s.SignerCertificate.Subject}|ConvertTo-Json -Compress"
    result = subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', command], capture_output=True, check=True)
    signature = json.loads(result.stdout.decode('utf-8-sig'))
    assert signature['status'] == 'Valid' and 'CN=Microsoft Corporation,' in signature['publisher']
    summary = json.loads((CANDIDATE / 'verification/summary.json').read_text(encoding='utf-8'))
    assert summary['total'] == 464
    expected = json.loads((CANDIDATE / 'verification/binary-sha256.json').read_text(encoding='utf-8'))
    for name, digest in expected.items():
        assert sha(CANDIDATE / name) == digest, name
    OUTPUT.mkdir(parents=True)
    for path in CANDIDATE.iterdir():
        if path.is_file() and path.suffix in {'.exe', '.dll', '.config', '.json'}:
            name = path.name.replace('WorkAssistant.Desktop.exe', MAIN_EXE)
            shutil.copy2(path, OUTPUT / name)
    for folder in ['Assets', 'x86', 'x64', 'skills', 'samples', 'project-configuration', 'third-party-licenses', 'docs', 'verification']:
        shutil.copytree(CANDIDATE / folder, OUTPUT / folder)
    shutil.copy2(ROOT / 'docs/THIRD_PARTY_NOTICES.md', OUTPUT / 'THIRD_PARTY_NOTICES.md')
    assert sha(OUTPUT / MAIN_EXE) == expected['WorkAssistant.Desktop.exe']
    assert sha(OUTPUT / 'app.defaults.json') == sha(ROOT / 'config/app.defaults.json')
    for arch in ['x86', 'x64']:
        assert sha(OUTPUT / arch / 'SQLite.Interop.dll') == sha(CANDIDATE / arch / 'SQLite.Interop.dll')
    assert len(list((OUTPUT / 'skills').glob('*/skill.json'))) == 11
    text(OUTPUT / 'portable.mode', 'portable-v1\n', 'ascii')
    text(OUTPUT / '准备运行环境.cmd', (ROOT / 'installer/Start-Xiao-Portable.cmd').read_text(encoding='utf-8'), 'ascii')
    text(OUTPUT / '检查运行环境.cmd', '@echo off\ncall "%~dp0Start-Xiao.cmd" --check\npause\n', 'ascii')
    # The ASCII alias avoids depending on the target Windows console's active code page.
    text(OUTPUT / 'Start-Xiao.cmd', (ROOT / 'installer/Start-Xiao-Portable.cmd').read_text(encoding='utf-8'), 'ascii')
    text(OUTPUT / '首次使用说明.txt', (ROOT / 'installer/PORTABLE_README_1.0.1.txt').read_text(encoding='utf-8'), 'utf-8-sig')
    prerequisite = OUTPUT / 'prerequisites'; prerequisite.mkdir()
    shutil.copy2(INSTALLER, prerequisite / INSTALLER.name)
    source = {'product': '.NET Framework 4.8 x86/x64 offline runtime',
              'source': 'https://dotnet.microsoft.com/en-us/download/dotnet-framework/thank-you/net48-offline-installer',
              'sha256': INSTALLER_SHA, 'signature': signature,
              'installation_executed_during_packaging': False}
    text(prerequisite / 'source.json', json.dumps(source, indent=2))
    for path in OUTPUT.rglob('*'):
        assert not path.is_symlink(), path
        assert path.name not in {'用户数据', 'settings.json', 'credentials', 'qa', 'data', 'tasks'}, path
        assert path.suffix.lower() not in {'.pdb', '.sqlite3', '.db', '.jsonl', '.log', '.tmp', '.bak'}, path
    binaries = {p.name: sha(p) for p in OUTPUT.iterdir() if p.suffix in {'.exe', '.dll'}}
    text(OUTPUT / 'verification/candidate-binary-sha256.json', json.dumps(expected, indent=2))
    text(OUTPUT / 'verification/binary-sha256.json', json.dumps(binaries, indent=2, ensure_ascii=False))
    metadata = {'version': '1.0.1', 'package': NAME, 'main_executable': MAIN_EXE,
                'data_mode': 'isolated portable data, no automatic legacy AppData import',
                'data_directory': '用户数据', 'default_workspace': 'Documents/晓助手工作区',
                'delivery_directory': 'workspace/output', 'personal_data_included': False,
                'default_skills': 11, 'checks_passed': summary['total'],
                'application_authenticode_signed': False, 'win7_physical_acceptance': 'pending'}
    text(OUTPUT / 'baseline.json', json.dumps(metadata, indent=2, ensure_ascii=False))
    files = sorted(p for p in OUTPUT.rglob('*') if p.is_file())
    hashes = {p.relative_to(OUTPUT).as_posix(): sha(p) for p in files}
    text(OUTPUT / 'SHA256SUMS.txt', '\n'.join(value + '  ' + name for name, value in hashes.items()) + '\n')
    with zipfile.ZipFile(archive_path, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(p for p in OUTPUT.rglob('*') if p.is_file()):
            compression = zipfile.ZIP_STORED if path.name == INSTALLER.name else zipfile.ZIP_DEFLATED
            archive.write(path, path.relative_to(PACKAGES).as_posix(), compress_type=compression)
    with zipfile.ZipFile(archive_path) as archive:
        assert archive.testzip() is None
        assert len(archive.namelist()) == len(hashes) + 1
        for name, digest in hashes.items():
            assert hashlib.sha256(archive.read(NAME + '/' + name)).hexdigest() == digest, name
    digest = sha(archive_path)
    text(PACKAGES / (NAME + '.zip.sha256'), digest + '  ' + archive_path.name + '\n', 'ascii')
    print(json.dumps({'zip': str(archive_path), 'bytes': archive_path.stat().st_size,
                      'files': len(hashes) + 1, 'sha256': digest}, indent=2))


if __name__ == '__main__':
    main()
