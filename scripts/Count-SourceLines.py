"""Count physical/nonblank lines without executing application or tests."""
from collections import defaultdict
from pathlib import Path
import json

root = Path(__file__).resolve().parents[1]
rows = []
for folder, category, suffixes in [('src', 'production', {'.cs', '.xaml'}), ('tests', 'tests', {'.cs', '.xaml'}),
                                  ('scripts', 'build', {'.py', '.ps1'}), ('installer', 'build', {'.iss'})]:
    for path in sorted((root / folder).rglob('*')):
        if not path.is_file() or path.suffix.lower() not in suffixes or {'bin', 'obj', '__pycache__'}.intersection(path.relative_to(root).parts):
            continue
        if path.name.endswith(('.g.cs', '.g.i.cs', '.AssemblyInfo.cs')):
            continue
        lines = path.read_text(encoding='utf-8-sig').splitlines()
        rows.append({'path': path.relative_to(root).as_posix(), 'category': category,
                     'project': path.relative_to(root).parts[1] if folder in {'src', 'tests'} else folder,
                     'extension': path.suffix, 'physical': len(lines), 'nonblank': sum(bool(line.strip()) for line in lines)})
groups = defaultdict(lambda: {'files': 0, 'physical': 0, 'nonblank': 0})
for row in rows:
    group = groups[(row['category'], row['project'])]
    group['files'] += 1
    group['physical'] += row['physical']
    group['nonblank'] += row['nonblank']
totals = {kind: {'physical': sum(r['physical'] for r in rows if r['category'] == kind),
                 'nonblank': sum(r['nonblank'] for r in rows if r['category'] == kind),
                 'files': sum(r['category'] == kind for r in rows)} for kind in ['production', 'tests', 'build']}
document = ['# 软件代码行数统计', '', '口径：生产源码仅统计 src 中手写的 .cs 与 .xaml。物理行数含空行和注释；非空行数仍含注释，不等同于逻辑语句数。',
            '排除 bin、obj、生成代码、依赖、构建制品、旧版本副本、样例数据、技能 Markdown、文档及项目配置。测试与构建脚本单独列出；统计没有运行任何测试。', '',
            '| 类别 | 项目 | 文件数 | 物理行数 | 非空行数 |', '|---|---|---:|---:|---:|']
for (category, project), value in sorted(groups.items()):
    document.append(f'| {category} | {project} | {value["files"]} | {value["physical"]} | {value["nonblank"]} |')
document += ['', '| 汇总 | 文件数 | 物理行数 | 非空行数 |', '|---|---:|---:|---:|']
for category, value in totals.items():
    document.append(f'| {category} | {value["files"]} | {value["physical"]} | {value["nonblank"]} |')
document += ['', '明细见 SOURCE_LINES.json。复算：`python scripts/Count-SourceLines.py`。', '']
(root / 'docs/SOURCE_LINES.md').write_text('\n'.join(document), encoding='utf-8')
(root / 'docs/SOURCE_LINES.json').write_text(json.dumps({'totals': totals, 'files': rows}, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(totals, ensure_ascii=False))
