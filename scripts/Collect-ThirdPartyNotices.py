"""Collect shipped NuGet dependency metadata and bundled license/notice files; does not execute packages."""
import json
import shutil
import xml.etree.ElementTree as ET
from pathlib import Path

root = Path(__file__).resolve().parents[1]
assets = json.loads((root / 'src/WorkAssistant.Desktop/obj/project.assets.json').read_text(encoding='utf-8-sig'))
cache = Path(next(iter(assets['packageFolders'])))
license_root = root / 'docs/third-party-licenses'
license_root.mkdir(parents=True, exist_ok=True)
lines = ['# 第三方组件清单', '', '来源：当前 NuGet 还原产生的 project.assets.json 与各包内 nuspec。开发框架引用包不随应用分发，未列入。', '',
         '| 组件 | 版本 | 许可证声明 | 项目/许可证来源 |', '|---|---|---|---|']
for key, data in sorted(assets['libraries'].items(), key=lambda item: item[0].lower()):
    if data.get('type') != 'package' or key.lower().startswith('microsoft.netframework.referenceassemblies'):
        continue
    package = cache / data['path']
    specs = list(package.glob('*.nuspec'))
    if not specs:
        continue
    metadata = ET.parse(specs[0]).getroot()
    values = {e.tag.rsplit('}', 1)[-1]: e for e in metadata.iter()}
    def value(name):
        element = values.get(name)
        return element.text.strip() if element is not None and element.text else ''
    name, version = key.rsplit('/', 1)
    license_name = value('license') or '见组件随附声明及来源链接'
    source = value('projectUrl') or value('licenseUrl')
    lines.append(f'| {name} | {version} | {license_name.replace("|", "/")} | {source} |')
    target = license_root / f'{name}-{version}'
    target.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(specs[0], target / specs[0].name)
    paths = set(package.glob('*LICENSE*')) | set(package.glob('*license*')) | set(package.glob('*NOTICE*')) | set(package.glob('*notice*'))
    element = values.get('license')
    if element is not None and element.attrib.get('type') == 'file':
        paths.add(package / value('license'))
    for path in paths:
        if path.is_file() and path.stat().st_size < 1024 * 1024:
            shutil.copyfile(path, target / path.name)
lines += ['', '完整的包内许可证与 nuspec 副本见 third-party-licenses/。SPDX 许可证正文另附在该目录根部。', '',
          'SQLite、SkiaSharp/HarfBuzz 和 Tesseract/Leptonica 随包提供 x86/x64 本机组件；OCR 语言资源来自 tesseract-ocr/tessdata_fast 4.1.0（Apache-2.0），来源与散列见 ocr/sources.json，许可证见 ocr/tessdata/LICENSE。VC++ 2019 运行库由微软安装包提供。实际 Win7 加载验证属于尚未执行的验证工作。', '']
(root / 'docs/THIRD_PARTY_NOTICES.md').write_text('\n'.join(lines), encoding='utf-8')
