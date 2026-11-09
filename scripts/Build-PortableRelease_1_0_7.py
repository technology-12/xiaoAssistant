"""Ship only tested 1.0.7 binaries, with app-local VC and generated evidence.

No replacement of existing releases; no system installation; no manual test counts.
"""
import argparse,hashlib,json,os,shutil,subprocess,zipfile
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
BUILD=ROOT/'artifacts/repair-1.0.7'
PACKAGES=ROOT/'artifacts/packages'
BUILD_ID='xiao-1.0.7-local-runtime-r1'
PRODUCTS=['WorkAssistant.Desktop.exe','WorkAssistant.Host.exe','WorkAssistant.Core.dll',
          'WorkAssistant.Infrastructure.dll','WorkAssistant.Tools.dll','WorkAssistant.OpenCode.dll']
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def write(path,value):
    path.parent.mkdir(parents=True,exist_ok=True)
    text=json.dumps(value,ensure_ascii=False,indent=2) if not isinstance(value,str) else value
    path.write_text(text+'\n',encoding='utf-8')
def evidence(line):
    reports={}
    for suite in ('console','ui'):
        path=BUILD/'verification'/(line+'-'+suite+'.json');r=json.loads(path.read_text(encoding='utf-8-sig'))
        assert r['build_id']==BUILD_ID and r['failed']==0 and r['total']>0
        assert r['total']==r['passed']+r['failed']+r.get('skipped',0)
        if 'cases' in r:assert len(r['cases'])==r['total'] and sum(c['status']=='passed' for c in r['cases'])==r['passed']
        for name,digest in r['binaries'].items():assert sha(BUILD/line/name)==digest,(line,suite,name)
        for name in PRODUCTS:assert name in r['binaries'] or suite=='console' and name=='WorkAssistant.Desktop.exe'
        reports[suite]=r
    return reports
def signed_installer(xp):
    name='dotNetFx40_Full_x86_x64.exe' if xp else 'NDP48-x86-x64-AllOS-ENU.exe'
    path=ROOT/'artifacts/prerequisites'/name
    expected='65e064258f2e418816b304f646ff9e87af101e4c9552ab064bb74d281c38659f' if xp else '0a3a390c47e639d0f7fc65b21195fee6b7f65b066f80f70c60fab191d14b7e40'
    assert sha(path)==expected,'Pinned .NET installer changed'
    command="$s=Get-AuthenticodeSignature -LiteralPath '"+str(path).replace("'","''")+"';@{status=$s.Status.ToString();publisher=$s.SignerCertificate.Subject}|ConvertTo-Json -Compress"
    result=subprocess.check_output(['powershell.exe','-NoProfile','-NonInteractive','-Command',command],env={**os.environ,'PSModulePath':os.environ['SystemRoot']+r'\System32\WindowsPowerShell\v1.0\Modules'})
    signature=json.loads(result.decode('utf-8-sig'));assert signature['status']=='Valid' and 'CN=Microsoft Corporation,' in signature['publisher']
    return path,signature
def package(line):
    xp=line.startswith('xp-');arch=line[3:] if xp else None
    reports=evidence(line);source=BUILD/line
    name='XiaoAssistant-1.0.7-portable'+('-windows-xp-'+arch if xp else '')
    output=PACKAGES/name;archive=PACKAGES/(name+'.zip')
    if output.exists() or archive.exists():raise SystemExit('Output already exists: '+name+'; preserve it and select a new build.')
    payload=output/'程序文件';payload.mkdir(parents=True)
    for p in source.iterdir():
        if p.is_file() and p.suffix.lower() in {'.dll','.exe','.config'} and not p.name.startswith(('WorkAssistant.Tests.','Xiao.Launcher','OcrAcceptance','QuickProbe','PdfProbe')):
            shutil.copy2(p,payload/p.name)
    for name2 in ('app.defaults.json','kernel.policy.json'):shutil.copy2(ROOT/'config'/name2,payload/name2)
    if xp:
        audit=json.loads((BUILD/'verification/xp-pe-gates.json').read_text(encoding='utf-8'))
        assert audit['build_id']==BUILD_ID and audit['failed']==0 and audit['passed']>=5
        for n,h in audit['binaries'].items():assert sha(BUILD/n)==h,n
        shutil.copytree(source/arch,payload/arch)
        assert (payload/'WorkAssistant.Compatibility.Xp.dll').is_file()
    else:
        for a in ('x86','x64'):shutil.copytree(source/a,payload/a)
        shutil.copytree(source/'ocr',payload/'ocr')
        shutil.copy2(ROOT/'assets/ocr/sources.json',payload/'ocr/sources.json')
        ocr_assets=json.loads((payload/'ocr/sources.json').read_text(encoding='utf-8'))
        for item in ocr_assets['files']:assert sha(payload/'ocr/tessdata'/item['file'])==item['sha256']
        shutil.copy2(source/'native-runtime.json',payload/'native-runtime.json')
        native=json.loads((payload/'native-runtime.json').read_text(encoding='utf-8'))
        for n,h in native['files'].items():assert sha(payload/n)==h
        ocr=json.loads((BUILD/'verification/native-acceptance.json').read_text(encoding='utf-8'))
        assert ocr['build_id']==BUILD_ID and ocr['failed']==0 and set(ocr['architectures'])=={'x86','x64'}
        for n,h in ocr['binaries'].items():assert sha(source/n)==h,n
        reports['native']=ocr
    for folder in ('skills','samples','project-configuration'):
        origin=ROOT/'examples'/folder if folder=='project-configuration' else ROOT/folder
        shutil.copytree(origin,payload/folder)
    if xp:
        skill=payload/'skills/pdf-extract';m=json.loads((skill/'skill.json').read_text(encoding='utf-8-sig'))
        m['Description']='读取 PDF 原有文字层，保留页码；XP 版不支持扫描件 OCR。';m['Version']='1.0.7-xp'
        write(skill/'skill.json',m)
        write(skill/'SKILL.md','---\nname: pdf-extract\ndescription: 读取文本型 PDF，保留页码；XP 版不支持扫描件 OCR。\n---\n\n使用 extract_pdf_text、read_document_text、search_input_text 分页读取和检索。检查 partial_document 与 has_more；不得将预览当全文。本版本不含 OCR，扫描件须明确说明不支持，不得编造原文。')
    if (source/'Assets').exists():shutil.copytree(source/'Assets',payload/'Assets')
    licenses=payload/'third-party-licenses';shutil.copytree(ROOT/'artifacts/ocr-candidate/third-party-licenses',licenses)
    if not xp:shutil.copy2(ROOT/'artifacts/vc-app-local/MICROSOFT-VC-LICENSE.rtf',licenses/'MICROSOFT-VC-LICENSE.rtf')
    shutil.copy2(ROOT/'docs/THIRD_PARTY_NOTICES.md',payload/'THIRD_PARTY_NOTICES.md')
    docs=payload/'docs';docs.mkdir()
    for path in (ROOT/'docs').glob('*.md'):
        if path.name.startswith(('DOCUMENT_WORKFLOW','CONTEXT_PERMISSIONS','RELEASE_1.0.7')):shutil.copy2(path,docs/path.name)
    shutil.copy2(source/'Xiao.Launcher.exe',output/'晓助手.exe')
    for extra in ('Xiao.Launcher.exe.config',):
        if (source/extra).exists():shutil.copy2(source/extra,payload/extra)
    write(payload/'portable.mode','parent-v1-xp' if xp else 'parent-v1')
    helper=ROOT/('installer/xp/Start-Xiao-Xp.cmd' if xp else 'installer/Start-Xiao-Ocr.cmd')
    (payload/'Start-Xiao.cmd').write_bytes(helper.read_text(encoding='utf-8').replace('\r\n','\n').replace('\n','\r\n').encode('ascii'))
    wrapper='@echo off\r\nsetlocal EnableExtensions DisableDelayedExpansion\r\nset "helper="\r\nfor /d %%D in ("%~dp0*") do if exist "%%~fD\\Start-Xiao.cmd" set "helper=%%~fD\\Start-Xiao.cmd"\r\nif not defined helper exit /b 4\r\n'
    for check,label in ((True,'检查运行环境.cmd'),(False,'准备运行环境.cmd')):
        args=(' --check' if check else ' --prepare')+(' '+arch if xp else '')
        body=wrapper+'call "%helper%"'+args+'\r\nset "result=%errorlevel%"\r\n'+('pause\r\n' if check else '')+'exit /b %result%\r\n'
        (output/label).write_bytes(body.encode('ascii'))
    readme=(ROOT/('installer/xp/PORTABLE_README_1.0.7_XP.txt' if xp else 'installer/PORTABLE_README_1.0.7.txt')).read_text(encoding='utf-8')
    readme=readme.format(console_passed=reports['console']['passed'],console_total=reports['console']['total'],ui_passed=reports['ui']['passed'],ui_total=reports['ui']['total'])
    (output/'首次使用说明.txt').write_text(readme,encoding='utf-8-sig')
    installer,signature=signed_installer(xp);prereq=payload/'prerequisites';prereq.mkdir();shutil.copy2(installer,prereq/installer.name)
    write(prereq/'source.json',{'file':installer.name,'sha256':sha(installer),'signature':signature,'installation_executed_during_packaging':False,'vc_system_installation_required':False})
    verification=payload/'verification';verification.mkdir()
    for suite,r in reports.items():write(verification/(suite+'.json'),r)
    if xp:write(verification/'pe-gates.json',audit)
    counters={s:{k:r[k] for k in ('total','passed','failed','skipped') if k in r} for s,r in reports.items()}
    product_files=[p for p in payload.rglob('*') if p.is_file() and p.suffix.lower() in ('.dll','.exe')]
    frozen={p.relative_to(payload).as_posix():sha(p) for p in product_files}
    frozen['../晓助手.exe']=sha(output/'晓助手.exe')
    # Compare shipped application/native bytes with the exact ones covered by the test reports.
    for n,h in reports['console']['binaries'].items():
        target=output/'晓助手.exe' if n=='Xiao.Launcher.exe' else payload/n
        if target.exists():assert sha(target)==h,n
    write(verification/'build-manifest.json',{'build_id':BUILD_ID,'binaries':frozen})
    write(verification/'parity-evidence.json',{'build_id':BUILD_ID,'line':line,'suites':counters,'source':'machine-generated test reports; no manual counts','real_xp_acceptance':'pending' if xp else 'not applicable'})
    write(payload/'baseline.json',{'version':'1.0.7','build_id':BUILD_ID,'package':name,'product_line':'windows-xp' if xp else 'net48',
         'runtime':'.NET Framework 4 Full' if xp else '.NET Framework 4.8','vc_deployment':'not needed (static native SQLite)' if xp else 'application-local',
         'personal_data_included':False,'ocr_included':not xp,'release_ready':False,'local_regressions_passed':True,
         'xp_physical_acceptance':'pending' if xp else 'not applicable','win7_physical_acceptance':'pending' if not xp else 'not applicable'})
    assert {p.name for p in output.iterdir() if p.is_file()}=={'晓助手.exe','检查运行环境.cmd','准备运行环境.cmd','首次使用说明.txt'}
    for p in output.rglob('*'):
        assert not p.is_symlink() and p.name not in {'用户数据','settings.json','credentials','qa','data','tasks'}
        assert p.suffix.lower() not in {'.pdb','.sqlite3','.db','.jsonl','.log','.tmp','.bak'}
    files={p.relative_to(output).as_posix():sha(p) for p in output.rglob('*') if p.is_file()}
    write(payload/'SHA256SUMS.txt','\n'.join(h+'  '+n for n,h in sorted(files.items())))
    with zipfile.ZipFile(archive,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=6) as z:
        for p in sorted(output.rglob('*')):
            if p.is_file():z.write(p,p.relative_to(PACKAGES).as_posix(),compress_type=zipfile.ZIP_STORED if p.name==installer.name else zipfile.ZIP_DEFLATED)
    with zipfile.ZipFile(archive) as z:
        assert z.testzip() is None
        for n,h in files.items():assert hashlib.sha256(z.read(name+'/'+n)).hexdigest()==h,n
    digest=sha(archive);write(PACKAGES/(name+'.zip.sha256'),digest+'  '+archive.name)
    return {'line':line,'package':str(archive),'sha256':digest,'bytes':archive.stat().st_size,'suites':counters}
def main():
    ap=argparse.ArgumentParser();ap.add_argument('--line',choices=['main','xp-x86','xp-x64','all'],default='all');args=ap.parse_args()
    results=[package(line) for line in (['main','xp-x86','xp-x64'] if args.line=='all' else [args.line])]
    write(BUILD/'verification/package-results.json',results);print(json.dumps(results,ensure_ascii=False,indent=2))
if __name__=='__main__':main()
