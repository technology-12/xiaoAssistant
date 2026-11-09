"""Package the verified usability release; collect only explicit application payloads."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NAME = 'XiaoAssistant-1.0.4-portable'
CANDIDATE = ROOT / 'artifacts/document-workflow-candidate'
PACKAGES = ROOT / 'artifacts/packages'
OUTPUT = PACKAGES / NAME
PAYLOAD = OUTPUT / '程序文件'
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
    assert summary['total'] >= 383
    assert summary['live_workloads_passed'] == [1, 5, 10]
    expected = json.loads((CANDIDATE / 'verification/binary-sha256.json').read_text(encoding='utf-8'))
    for name, digest in expected.items():
        assert sha(CANDIDATE / name) == digest, name
    PAYLOAD.mkdir(parents=True)
    for path in CANDIDATE.iterdir():
        if path.is_file() and path.suffix in {'.exe', '.dll', '.config', '.json'}:
            shutil.copy2(path, PAYLOAD / path.name)
    for folder in ['Assets', 'x86', 'x64', 'skills', 'samples', 'project-configuration', 'third-party-licenses', 'docs', 'verification']:
        shutil.copytree(CANDIDATE / folder, PAYLOAD / folder)
    shutil.copy2(ROOT / 'docs/THIRD_PARTY_NOTICES.md', PAYLOAD / 'THIRD_PARTY_NOTICES.md')
    assert sha(PAYLOAD / 'WorkAssistant.Desktop.exe') == expected['WorkAssistant.Desktop.exe']
    assert sha(PAYLOAD / 'app.defaults.json') == sha(ROOT / 'config/app.defaults.json')
    for arch in ['x86', 'x64']:
        assert sha(PAYLOAD / arch / 'SQLite.Interop.dll') == sha(CANDIDATE / arch / 'SQLite.Interop.dll')
    assert len(list((PAYLOAD / 'skills').glob('*/skill.json'))) == 11
    text(PAYLOAD / 'portable.mode', 'parent-v1\n', 'ascii')
    helper = '@echo off\nsetlocal EnableExtensions DisableDelayedExpansion\nset "helper="\nfor /d %%D in ("%~dp0*") do if exist "%%~fD\\Start-Xiao.cmd" set "helper=%%~fD\\Start-Xiao.cmd"\nif not defined helper (\n echo [ERROR] Extract the complete ZIP to a new folder.\n pause\n exit /b 4\n)\n'
    text(OUTPUT / '准备运行环境.cmd', helper + 'call "%helper%"\nexit /b %errorlevel%\n', 'ascii')
    text(OUTPUT / '检查运行环境.cmd', helper + 'call "%helper%" --check\nset "result=%errorlevel%"\npause\nexit /b %result%\n', 'ascii')
    text(PAYLOAD / 'Start-Xiao.cmd', (ROOT / 'installer/Start-Xiao-Portable.cmd').read_text(encoding='utf-8'), 'ascii')
    text(OUTPUT / '首次使用说明.txt', (ROOT / 'installer/PORTABLE_README_1.0.4.txt').read_text(encoding='utf-8'), 'utf-8-sig')
    launcher = ROOT / 'artifacts/document-workflow-launcher/Xiao.Launcher.exe'
    shutil.copy2(launcher, OUTPUT / MAIN_EXE)
    prerequisite = PAYLOAD / 'prerequisites'; prerequisite.mkdir()
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
    binaries = {p.name: sha(p) for p in PAYLOAD.iterdir() if p.suffix in {'.exe', '.dll'}}
    text(PAYLOAD / 'verification/candidate-binary-sha256.json', json.dumps(expected, indent=2))
    text(PAYLOAD / 'verification/binary-sha256.json', json.dumps(binaries, indent=2, ensure_ascii=False))
    metadata = {'version': '1.0.4', 'package': NAME, 'main_executable': MAIN_EXE,
                'data_mode': 'isolated portable data, no automatic legacy AppData import',
                'data_directory': '用户数据', 'default_workspace': 'Documents/晓助手工作区',
                'delivery_directory': 'workspace/output', 'personal_data_included': False,
                'default_skills': 11, 'checks_passed': summary['total'],
                'application_authenticode_signed': False, 'win7_physical_acceptance': 'pending', 'launcher_sha256': sha(launcher)}
    text(PAYLOAD / 'baseline.json', json.dumps(metadata, indent=2, ensure_ascii=False))
    assert {p.name for p in OUTPUT.iterdir() if p.is_file()} == {MAIN_EXE, '检查运行环境.cmd', '准备运行环境.cmd', '首次使用说明.txt'}
    files = sorted(p for p in OUTPUT.rglob('*') if p.is_file())
    hashes = {p.relative_to(OUTPUT).as_posix(): sha(p) for p in files}
    text(PAYLOAD / 'SHA256SUMS.txt', '\n'.join(value + '  ' + name for name, value in hashes.items()) + '\n')
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
