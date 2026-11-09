"""Exercise the real PE audit with valid/corrupted/wrong-bitness payloads."""
import hashlib,json,shutil,subprocess,sys,tempfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
BUILD=ROOT/'artifacts/repair-1.0.7'
work=Path(tempfile.mkdtemp(prefix='pe-gates-',dir=BUILD))
candidate=work/'candidate';binaries={}
for arch in ('x86','x64'):
    src=BUILD/('xp-'+arch);dst=candidate/arch;dst.mkdir(parents=True)
    for f in src.iterdir():
        if f.is_file() and f.suffix in {'.exe','.dll','.config'} and not f.name.startswith('WorkAssistant.Tests.'):
            shutil.copy2(f,dst/f.name);binaries['xp-'+arch+'/'+f.name]=hashlib.sha256(f.read_bytes()).hexdigest()
    for name in ('app.defaults.json','kernel.policy.json'):shutil.copy2(ROOT/'config'/name,dst/name)
    (dst/arch).mkdir();shutil.copy2(src/arch/'SQLite.Interop.dll',dst/arch/'SQLite.Interop.dll')
    binaries['xp-'+arch+'/'+arch+'/SQLite.Interop.dll']=hashlib.sha256((dst/arch/'SQLite.Interop.dll').read_bytes()).hexdigest()
results=[]
def audit(label,expected,source=candidate,contains=None):
    output=work/label;p=subprocess.run([sys.executable,str(ROOT/'scripts/Audit-XpPackage.py'),'--candidate',str(source),'--output',str(output)],capture_output=True)
    r=json.loads((output/'xp-package-pe-audit.json').read_text(encoding='utf-8'))
    valid=p.returncode==expected and (contains is None or any(contains in message for message in r['structural_problems']))
    results.append({'case':label,'passed':valid,'exit_code':p.returncode,'problems':r['structural_problems']})
    assert valid,(label,p.returncode,r['structural_problems'])
audit('valid',0)
host=candidate/'x86/WorkAssistant.Host.exe';original=host.read_bytes();host.write_bytes(b'NOT A PE FILE')
audit('corrupt-host',1,contains='PE');host.write_bytes(original)
for arch,other in (('x86','x64'),('x64','x86')):
    native=candidate/arch/arch/'SQLite.Interop.dll';original=native.read_bytes()
    shutil.copy2(candidate/other/other/'SQLite.Interop.dll',native)
    audit('wrong-native-'+arch,1,contains='native machine=');native.write_bytes(original)
empty=work/'empty';empty.mkdir();audit('empty-candidate',1,empty,contains='directory missing')
report={'build_id':'xiao-1.0.7-local-runtime-r1','passed':len(results),'failed':0,'total':len(results),'skipped':0,'cases':results,'binaries':binaries,'test_directory':str(work)}
(BUILD/'verification/xp-pe-gates.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({'passed':len(results),'failed':0}))
