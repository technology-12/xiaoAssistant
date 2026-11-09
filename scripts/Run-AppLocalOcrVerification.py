"""Verify both native bitnesses against the actual mainline payload; no installers."""
import concurrent.futures,hashlib,json,os,re,subprocess,sys
from pathlib import Path
from importlib.machinery import SourceFileLoader

ROOT=Path(__file__).resolve().parents[1]
verify=SourceFileLoader('release_verify',str(ROOT/'scripts/Run-ReleaseVerification.py')).load_module()
BUILD=verify.BUILD;folder=BUILD/'main';evidence=BUILD/'verification'
def run(arch):
    target=BUILD/'native-tests'/arch;target.mkdir(parents=True,exist_ok=True)
    temp=target/'tmp';temp.mkdir(exist_ok=True)
    env={**os.environ,'TEMP':str(temp),'TMP':str(temp)}
    p=subprocess.run([str(folder/('OcrAcceptance-'+arch+'.exe')),
         str(ROOT/'artifacts/diagnostics/ocr/x86/case-db5778/inputs/scan.pdf'),
         str(ROOT/'artifacts/diagnostics/ocr/fixtures'),str(target)],cwd=folder,env=env,capture_output=True,timeout=420)
    log=p.stdout.decode('utf-8-sig',errors='replace')+p.stderr.decode('utf-8-sig',errors='replace')
    (evidence/('native-'+arch+'.txt')).write_text(log,encoding='utf-8')
    passed=len(re.findall(r'^PASS ',log,re.M));failed=0 if p.returncode==0 and re.search(r'^PASSED \d+ bitness=',log,re.M) else 1
    print(json.dumps({'arch':arch,'passed':passed,'failed':failed,'exit_code':p.returncode}),flush=True)
    if failed:print(log[-2500:],flush=True)
    return {'architecture':arch,'passed':passed,'failed':failed,'total':passed+failed,'skipped':0}
before=verify.product_hashes(folder)
with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:results=list(pool.map(run,['x86','x64']))
assert verify.product_hashes(folder)==before
report={'build_id':verify.BUILD_ID,'architectures':['x86','x64'],'results':results,'binaries':before,
        'passed':sum(r['passed'] for r in results),'failed':sum(r['failed'] for r in results),'skipped':0,
        'system_installation':False,'native_modules_loaded_from_package':True,'model_api_requests':0,'actual_win7_acceptance':False}
report['total']=report['passed']+report['failed']
(evidence/'native-acceptance.json').write_text(json.dumps(report,indent=2),encoding='utf-8')
sys.exit(1 if report['failed'] else 0)
