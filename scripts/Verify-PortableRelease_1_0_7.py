"""Inspect ZIP bytes, then smoke-test fresh extracted copies without modifying release folders."""
import hashlib,json,os,subprocess,tempfile,zipfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];BUILD=ROOT/'artifacts/repair-1.0.7'
results=json.loads((BUILD/'verification/package-results.json').read_text(encoding='utf-8'))
target=Path(tempfile.mkdtemp(prefix='fresh-package-',dir=BUILD));records=[]
for item in results:
    path=Path(item['package']);name=path.stem
    assert hashlib.sha256(path.read_bytes()).hexdigest()==item['sha256']
    with zipfile.ZipFile(path) as z:
        assert z.testzip() is None
        for n in z.namelist():
            p=(target/n).resolve();assert target.resolve() in p.parents
        z.extractall(target)
    folder=target/name;payload=folder/'程序文件';frozen=json.loads((payload/'verification/build-manifest.json').read_text(encoding='utf-8'))
    for n,h in frozen['binaries'].items():assert hashlib.sha256((payload/n).read_bytes()).hexdigest()==h,n
    assert {p.name for p in folder.iterdir() if p.is_file()}=={'晓助手.exe','检查运行环境.cmd','准备运行环境.cmd','首次使用说明.txt'}
    assert not (folder/'用户数据').exists()
    assert not any('VC2019_redist' in p.name for p in folder.rglob('*'))
    assert not any(p.name.startswith(('WorkAssistant.Tests.','OcrAcceptance','QuickProbe')) for p in folder.rglob('*'))
    helper=payload/'Start-Xiao.cmd';args=['cmd.exe','/d','/c',str(helper),'--check']
    if item['line'].startswith('xp-'):args.append(item['line'][3:])
    envcheck=subprocess.run(args,cwd=payload,capture_output=True,timeout=20)
    assert envcheck.returncode==0,(item['line'],envcheck.stdout,envcheck.stderr)
    if item['line']=='main':
        probe=subprocess.run([str(ROOT/'artifacts/diagnostics/xp-review/MainlineCtorProbe.exe'),str(payload)],capture_output=True,timeout=20)
        assert probe.returncode==0 and probe.stdout.count(b'CONSTRUCTOR_OK')==2
    else:
        probe=subprocess.run([str(ROOT/('artifacts/diagnostics/xp-review-r3/PdfProbe-'+item['line'][3:]+'.exe')),str(payload),str(ROOT/'artifacts/diagnostics/xp-review-r3/fixtures/incremental.pdf')],capture_output=True,timeout=20)
        assert probe.returncode==0 and '甲' in probe.stdout.decode('utf-8-sig') and 'u2' in probe.stdout.decode('utf-8-sig')
    # Exercise the root launcher, child Host and delivered artifacts on the extracted bytes.
    ui=BUILD/'verification'/(item['line']+'-zip-ui.txt')
    p=subprocess.run([str(folder/'晓助手.exe'),'--selftest-ui','--result',str(ui)],cwd=folder,timeout=90,creationflags=0x08000000)
    content=ui.read_text(encoding='utf-8-sig');assert p.returncode==0 and '[FAIL]' not in content and 'UI SELFTEST PASS' in content
    assert hashlib.sha256(path.read_bytes()).hexdigest()==item['sha256']
    records.append({'line':item['line'],'sha256':item['sha256'],'frozen_binaries':len(frozen['binaries']),
                    'environment_check':True,'constructor_or_pdf_probe':True,'root_launcher_ui_pass':True,'ui_passed':content.count('[PASS]'),
                    'root_layout':'four files','personal_history_in_zip':False,'vc_installer_in_zip':False})
    print(json.dumps(records[-1]),flush=True)
(BUILD/'verification/zip-acceptance.json').write_text(json.dumps({'build_id':'xiao-1.0.7-local-runtime-r1','packages':records,'extraction_directory':str(target)},indent=2),encoding='utf-8')
