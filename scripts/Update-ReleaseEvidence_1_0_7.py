"""Generate release-note counts and package fingerprints from verification JSON."""
import hashlib,json,re
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1];V=ROOT/'artifacts/repair-1.0.7/verification'
rows=[];total=0
for line,label in [('main','主线 net48'),('xp-x86','XP x86'),('xp-x64','XP x64')]:
    c=json.loads((V/(line+'-console.json')).read_text(encoding='utf-8'));u=json.loads((V/(line+'-ui.json')).read_text(encoding='utf-8'))
    for r in (c,u):assert r['failed']==0 and r['total']==r['passed']+r['failed']+r['skipped']
    total+=c['total']+u['total'];rows.append(f"| {label} | {c['passed']}/{c['total']} | {u['passed']}/{u['total']} |")
n=json.loads((V/'native-acceptance.json').read_text(encoding='utf-8'));g=json.loads((V/'xp-pe-gates.json').read_text(encoding='utf-8'))
assert n['failed']==g['failed']==0;total+=n['total']+g['total']
section='''## 本机验证

以下计数由同一构建的机器可读结果生成，没有沿用旧版的手写计数。

| 产品线 | 控制台 | UI 自检 |
|---|---:|---:|
'''+ '\n'.join(rows)+f'''

VC++/OCR x86/x64 合计 {n['passed']}/{n['total']}，PE 发布门禁 {g['passed']}/{g['total']}；上述合计 {total} 项检查，失败 0。OCR 包含十页扫描件、任务缓存、取消、Host/Worker 与模块真实加载目录检查，未安装 VC++，未调用真实模型 API。

环境：Windows 10.0.26200 / CLR 4.8。产品源码构建 0 错误、0 警告；测试诊断编译的可空警告不计为产品构建错误。完整证据在 artifacts/repair-1.0.7/verification。真实 XP/Win7 验收范围见下节。

'''
path=ROOT/'docs/RELEASE_1.0.7.md';text=path.read_text(encoding='utf-8')
text=re.sub(r'## 本机验证\n[\s\S]*?(?=## 发布范围)',lambda m:section,text)
if '缩短持久化临时文件名' not in text:
    text=text.replace('## 本机验证','- 解压后复测发现并修复旧 Windows 长路径边界：缩短持久化临时文件名，保留完整随机标识与同卷替换；245 字符目标路径的更新/备份回归三线均通过。自检临时状态移到独立短目录，避免安装目录嵌套影响验收。\n\n## 本机验证')
packages=V/'package-results.json'
if packages.exists():
    data=json.loads(packages.read_text(encoding='utf-8'))
    if all(Path(p['package']).is_file() and hashlib.sha256(Path(p['package']).read_bytes()).hexdigest()==p['sha256'] for p in data):
        section='\n## 发行包指纹\n\n| 文件 | SHA-256 |\n|---|---|\n'
        for p in data:section+='| '+Path(p['package']).name+' | `'+p['sha256']+'` |\n'
        text=text.split('\n## 发行包指纹')[0]+section
path.write_text(text,encoding='utf-8');print(f'Release evidence generated: {total} checks, 0 failures.')
