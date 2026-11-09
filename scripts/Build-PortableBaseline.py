"""Freeze the already verified baseline; do not rebuild or copy user data."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NAME = 'XiaoAssistant-1.0.0-baseline7-20261104-portable'
CANDIDATE = ROOT / 'artifacts/iteration7-skills-candidate'
PACKAGES = ROOT / 'artifacts/packages'
OUTPUT = PACKAGES / NAME
INSTALLER = ROOT / 'artifacts/prerequisites/NDP48-x86-x64-AllOS-ENU.exe'
INSTALLER_SHA = '0a3a390c47e639d0f7fc65b21195fee6b7f65b066f80f70c60fab191d14b7e40'


def sha(path):
    with path.open('rb') as stream:
        value = hashlib.sha256()
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(block)
        return value.hexdigest()


def text(path, value, encoding='utf-8'):
    path.write_bytes(value.replace('\r\n', '\n').replace('\n', '\r\n').encode(encoding))


def main():
    if OUTPUT.exists() or (PACKAGES / (NAME + '.zip')).exists():
        raise SystemExit('Output already exists; preserve the frozen baseline and use a new package name.')
    assert sha(INSTALLER) == INSTALLER_SHA, 'Unexpected runtime installer hash'
    signature_script = (
        "$s=Get-AuthenticodeSignature -LiteralPath '" + str(INSTALLER).replace("'", "''") + "';"
        "@{status=$s.Status.ToString();publisher=$s.SignerCertificate.Subject;"
        "certificate_thumbprint=$s.SignerCertificate.Thumbprint}|ConvertTo-Json -Compress"
    )
    result = subprocess.run(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', signature_script],
                            capture_output=True, check=True)
    signature = json.loads(result.stdout.decode('utf-8-sig'))
    assert signature['status'] == 'Valid' and 'CN=Microsoft Corporation,' in signature['publisher']
    baseline_hashes = json.loads((CANDIDATE / 'verification/binary-sha256.json').read_text(encoding='utf-8'))
    for relative, expected in baseline_hashes.items():
        assert sha(CANDIDATE / relative) == expected, relative
    skill_hashes = json.loads((CANDIDATE / 'verification/skill-sha256.json').read_text(encoding='utf-8'))
    for relative, expected in skill_hashes.items():
        assert sha(CANDIDATE / relative) == expected, relative
    assert sha(CANDIDATE / 'app.defaults.json') == sha(ROOT / 'config/app.defaults.json')
    summary = json.loads((CANDIDATE / 'verification/summary.json').read_text(encoding='utf-8'))
    assert summary['total'] == 426
    app = OUTPUT / 'app'
    app.mkdir(parents=True)
    for path in CANDIDATE.iterdir():
        if path.is_file() and path.suffix in {'.exe', '.dll', '.config', '.json'}:
            shutil.copy2(path, app / path.name)
    for directory in ['Assets', 'x86', 'x64', 'skills', 'samples', 'project-configuration', 'third-party-licenses', 'docs', 'verification']:
        shutil.copytree(CANDIDATE / directory, app / directory)
    for architecture in ['x86', 'x64']:
        native = Path(architecture) / 'SQLite.Interop.dll'
        assert sha(app / native) == sha(CANDIDATE / native), native
    shutil.copy2(ROOT / 'docs/THIRD_PARTY_NOTICES.md', app / 'THIRD_PARTY_NOTICES.md')
    for path in app.rglob('*'):
        assert not path.is_symlink(), path
        assert path.name.lower() not in {'settings.json', 'state.sqlite3', 'credentials'}, path
        assert path.suffix.lower() not in {'.pdb', '.log', '.bak', '.tmp'}, path
    assert len(list((app / 'skills').glob('*/skill.json'))) == 11
    text(OUTPUT / 'Start-Xiao.cmd', (ROOT / 'installer/Start-Xiao.cmd').read_text(encoding='utf-8'), 'ascii')
    text(OUTPUT / 'Check-Environment.cmd',
         '@echo off\ncall "%~dp0Start-Xiao.cmd" --check\nset "result=%errorlevel%"\npause\nexit /b %result%\n', 'ascii')
    text(OUTPUT / 'README.txt', (ROOT / 'installer/PORTABLE_README.txt').read_text(encoding='utf-8'), 'utf-8-sig')
    prerequisite = OUTPUT / 'prerequisites'
    prerequisite.mkdir()
    shutil.copy2(INSTALLER, prerequisite / INSTALLER.name)
    source = {
        'product': 'Microsoft .NET Framework 4.8 Offline Runtime x86/x64',
        'page': 'https://dotnet.microsoft.com/en-us/download/dotnet-framework/thank-you/net48-offline-installer',
        'download_url': 'https://download.microsoft.com/download/f/3/a/f3a6af84-da23-40a5-8d1c-49cc10c8e76f/NDP48-x86-x64-AllOS-ENU.exe',
        'sha256': INSTALLER_SHA, 'bytes': INSTALLER.stat().st_size,
        'signature_verified': signature,
        'installation': 'Interactive Microsoft setup only if required; never executed during packaging.'
    }
    text(prerequisite / 'source.json', json.dumps(source, indent=2))
    metadata = {
        'package': NAME, 'baseline': 'iteration7-skills-candidate', 'date': '2026-11-04',
        'framework': '.NET Framework 4.8 (system prerequisite)',
        'deployment': 'Extract ZIP, run Start-Xiao.cmd; runtime setup if absent.',
        'personal_user_data_included': False, 'application_authenticode_signed': False,
        'baseline_checks_passed': summary['total'], 'default_skills': 11,
        'user_data_location': '%LOCALAPPDATA%/Win7WorkAssistant',
        'win7_physical_machine_acceptance': 'pending',
        'application_binaries_sha256': baseline_hashes
    }
    text(OUTPUT / 'baseline.json', json.dumps(metadata, indent=2))
    files = sorted(p for p in OUTPUT.rglob('*') if p.is_file())
    checksums = {p.relative_to(OUTPUT).as_posix(): sha(p) for p in files}
    text(OUTPUT / 'SHA256SUMS.txt', '\n'.join(f'{value}  {relative}' for relative, value in checksums.items()) + '\n')
    archive_path = PACKAGES / (NAME + '.zip')
    with zipfile.ZipFile(archive_path, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(p for p in OUTPUT.rglob('*') if p.is_file()):
            method = zipfile.ZIP_STORED if path.name == INSTALLER.name else zipfile.ZIP_DEFLATED
            archive.write(path, path.relative_to(PACKAGES).as_posix(), compress_type=method)
    with zipfile.ZipFile(archive_path) as archive:
        assert archive.testzip() is None
        assert len(archive.namelist()) == len(checksums) + 1
        for relative, expected in checksums.items():
            assert hashlib.sha256(archive.read(NAME + '/' + relative)).hexdigest() == expected, relative
    archive_sha = sha(archive_path)
    text(PACKAGES / (NAME + '.zip.sha256'), archive_sha + '  ' + archive_path.name + '\n', 'ascii')
    print(json.dumps({'zip': str(archive_path), 'bytes': archive_path.stat().st_size,
                      'sha256': archive_sha, 'files': len(checksums) + 1}, indent=2))


if __name__ == '__main__':
    main()
