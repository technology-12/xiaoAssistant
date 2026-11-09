"""Run local fake-model regressions with isolated portable data and binary-bound JSON evidence."""
import argparse,hashlib,json,os,re,subprocess,sys
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
BUILD=ROOT/'artifacts/repair-1.0.7'
BUILD_ID='xiao-1.0.7-local-runtime-r1'
def hashes(folder):
    files=list(folder.glob('*.dll'))+list(folder.glob('*.exe'))
    for arch in ('x86','x64'):files+=list((folder/arch).glob('*.dll'))
    return {p.relative_to(folder).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(files)}
def product_hashes(folder):
    return {n:h for n,h in hashes(folder).items() if not Path(n).name.startswith(('WorkAssistant.Tests.','OcrAcceptance','QuickProbe','PdfProbe'))}
def main():
    ap=argparse.ArgumentParser();ap.add_argument('line',choices=['main','xp-x86','xp-x64']);ap.add_argument('--ui',action='store_true');args=ap.parse_args()
    folder=BUILD/args.line; evidence=BUILD/'verification';evidence.mkdir(exist_ok=True)
    (folder/'portable.mode').write_text('local-v1',encoding='ascii')
    temp=BUILD/'test-temp'/args.line;temp.mkdir(parents=True,exist_ok=True)
    env={**os.environ,'TEMP':str(temp),'TMP':str(temp),'WORKASSISTANT_BUILD_ID':BUILD_ID,
         'WORKASSISTANT_TEST_REPORT':str(evidence/(args.line+'-console.json'))}
    before=hashes(folder)
    if args.ui:
        log=evidence/(args.line+'-ui.txt')
        p=subprocess.run([str(folder/'WorkAssistant.Desktop.exe'),'--selftest-ui','--result',str(log)],cwd=folder,env=env,timeout=180,creationflags=0x08000000)
        content=log.read_text(encoding='utf-8-sig');passed=len(re.findall(r'^\[PASS\]',content,re.M));failed=len(re.findall(r'^\[FAIL\]',content,re.M))
        report={'build_id':BUILD_ID,'total':passed+failed,'passed':passed,'failed':failed,'skipped':0,'binaries':product_hashes(folder),'exit_code':p.returncode}
        (evidence/(args.line+'-ui.json')).write_text(json.dumps(report,indent=2),encoding='utf-8')
    else:
        p=subprocess.run([str(folder/'WorkAssistant.Tests.Console.exe')],cwd=folder,env=env,capture_output=True,timeout=240)
        content=p.stdout.decode('utf-8-sig',errors='replace')+p.stderr.decode('utf-8-sig',errors='replace')
        content=re.sub(r'sk-[A-Za-z0-9_-]+','[REDACTED]',content)
        (evidence/(args.line+'-console.txt')).write_text(content,encoding='utf-8')
        report=json.loads((evidence/(args.line+'-console.json')).read_text(encoding='utf-8-sig'))
        assert all(before[k]==v for k,v in report['binaries'].items())
    assert hashes(folder)==before,'Product binaries changed during verification'
    print(json.dumps({k:v for k,v in report.items() if k not in ('binaries','cases')},ensure_ascii=False))
    lines=content.splitlines()
    for i,line in enumerate(lines):
        if line.startswith('[FAIL]'):print('\n'.join(lines[i:i+2]))
    return p.returncode or (1 if report['failed'] else 0)
if __name__=='__main__':sys.exit(main())
