"""Physical source lines; separates owned code, upstream runtime, other upstream products, and tests."""
from pathlib import Path
from collections import defaultdict
import json

root = Path(__file__).resolve().parents[2]
upstream = root / 'migration/upstream/opencode'
suffixes = {'.cs', '.xaml', '.ts', '.tsx', '.js', '.jsx', '.mjs', '.rs', '.css', '.sql', '.py', '.ps1'}
excluded = {'node_modules', '.git', 'bin', 'obj', 'dist', 'target', '.local', '__pycache__', 'vendor'}
groups = defaultdict(lambda: {'files': 0, 'physical': 0, 'nonblank': 0})
rows = []
for base in [root / 'src', root / 'migration/gateway', root / 'migration/scripts', root / 'migration/verification', upstream]:
    for p in sorted(base.rglob('*')):
        if not p.is_file() or p.suffix not in suffixes or excluded.intersection(p.relative_to(base).parts):
            continue
        rel = p.relative_to(root)
        if any(marker in p.name for marker in ['.gen.', '.g.cs', '.g.i.cs', '.AssemblyInfo.', '.d.ts']):
            continue
        is_test = any(part.lower() in {'test', 'tests', '__tests__', 'fixtures', 'verification'} for part in rel.parts) or '.test.' in p.name or '.spec.' in p.name
        if p.is_relative_to(upstream):
            inner = p.relative_to(upstream).as_posix()
            category = 'upstream_tests' if is_test else 'upstream_runtime' if inner.startswith('packages/opencode/src/') else 'upstream_other'
        else:
            category = 'owned_verification' if is_test else 'owned_production' if p.is_relative_to(root / 'src') or p.is_relative_to(root / 'migration/gateway/src') else 'owned_build'
        lines = p.read_text(encoding='utf-8-sig', errors='replace').splitlines()
        row = {'path': rel.as_posix(), 'category': category, 'physical': len(lines), 'nonblank': sum(bool(line.strip()) for line in lines)}
        rows.append(row)
        groups[category]['files'] += 1
        groups[category]['physical'] += row['physical']
        groups[category]['nonblank'] += row['nonblank']

result = {'method': 'Physical lines including blanks/comments; generated code, dependencies, locks and docs excluded',
          'upstream_commit': '09ff3b9bb925903b36aa35fec25394133a42cf6d', 'groups': dict(groups), 'files': rows}
out = root / 'migration/SOURCE_SCALE.json'
out.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
markdown = ['# 新工程源码规模', '', '统计口径：物理行数，包括注释和空行；排除依赖、生成代码、锁文件、文档及构建输出。', '',
            '| 类别 | 文件 | 物理行 | 非空行 |', '|---|---:|---:|---:|']
for name, stats in groups.items():
    markdown.append(f"| {name} | {stats['files']} | {stats['physical']:,} | {stats['nonblank']:,} |")
prod = sum(groups[n]['physical'] for n in ['owned_production', 'upstream_runtime', 'upstream_other'])
runtime = groups['owned_production']['physical'] + groups['upstream_runtime']['physical']
markdown += ['', f'完整生产源码基线：**{prod:,} 行**。', f'自有生产代码 + OpenCode 运行包 src：**{runtime:,} 行**。', '',
             '完整基线含上游其他界面、共享库和工具，不代表它们都在当前晓助手中运行；上游代码不计作自行研发。运行包 src 也包含 CLI/TUI，不能全部称为纯 Agent 循环。',
             '本机复用官方固定版本二进制完成集成；未从此源码树重新编译上游。精确逐文件明细见 SOURCE_SCALE.json。']
(root / 'migration/SOURCE_SCALE.md').write_text('\n'.join(markdown) + '\n', encoding='utf-8')
print(json.dumps({'groups': dict(groups), 'production_total': prod, 'owned_plus_runtime': runtime}, ensure_ascii=False))
