"""Inventory XP migration blockers. A source audit is not an XP runtime test.

No credentials, user databases, bin/obj or upstream repositories are read.
Default: write reports and return 0. --check: return 2 unless release gates pass.
This B0 implementation deliberately cannot authorize an XP release: there is
no physical/VM execution evidence verifier yet.
"""
import argparse
import hashlib
import json
import re
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
BASELINE = ROOT / "baselines/windows-xp/baseline.json"
RULES = [
    ("XP001", r"\bIReadOnly(?:List|Dictionary|Collection)\s*<", "runtime", "Replace .NET 4.5+ collection contracts"),
    ("XP002", r"\bArray\.Empty\s*<", "runtime", "Provide compatible empty arrays"),
    ("XP003", r"\bTask\.(?:Run|WhenAll|WhenAny|Delay|FromResult)\s*[(<]", "async", "Port task scheduling and coordination"),
    ("XP004", r"\b(?:WaitAsync|CancelAfter)\s*\(", "async", "Port cancellation and asynchronous locks"),
    ("XP005", r"\bRunContinuationsAsynchronously\b", "async", "Preserve non-inline continuation semantics"),
    ("XP006", r"\bAsyncLocal\s*<", "async", "Preserve per-agent asynchronous execution context"),
    ("XP007", r"\bHttpClient(?:Handler)?\b", "network", "Replace transport and validate TLS on XP"),
    ("XP008", r"\b(?:ConnectAsync|WaitForConnectionAsync)\s*\(", "ipc", "Port named-pipe connection and cancellation"),
    ("XP009", r"\bGetFinalPathNameByHandle(?:W)?\b", "filesystem", "Vista+ API: redesign handle/path verification"),
    ("XP010", r"\bSetFileInformationByHandle\b", "filesystem", "Vista+ API: redesign verified atomic file commits"),
    ("XP011", r"\bGetNamedPipeClientProcessId\b", "ipc", "Vista+ API: redesign worker authentication"),
    ("XP012", r"\bMarshal\.SizeOf\s*<", "runtime", "Use compatible interop sizing and validate x86/x64 layouts"),
    ("XP013", r"\bZipArchive(?:Entry|Mode)?\b", "office", "Replace .NET 4.5+ ZIP APIs without weakening archive limits"),
    ("XP014", r"VirtualizingPanel\.(?:ScrollUnit|CacheLength)", "ui", "Replace newer WPF scrolling properties"),
    ("XP015", r"\b(?:ValueTuple|AssemblyMetadataAttribute)\b", "runtime", "Audit later framework type dependencies"),
]


def source_files(root):
    return sorted(p for p in (root / "src").rglob("*")
                  if p.is_file() and not {"bin", "obj"}.intersection(p.relative_to(root / "src").parts))


def scan(files, root):
    findings = []
    for path in files:
        if path.suffix not in {".cs", ".xaml", ".csproj", ".manifest", ".config"}:
            continue
        lines = path.read_text(encoding="utf-8-sig").splitlines()
        for number, line in enumerate(lines, 1):
            for rule_id, pattern, area, action in RULES:
                if re.search(pattern, line):
                    # Emit locations and rule names, never source text or configuration values.
                    findings.append(dict(rule=rule_id, area=area, file=path.relative_to(root).as_posix(),
                                         line=number, action=action))
    return findings


def dependencies(files):
    packages = {}
    cache = Path.home() / ".nuget/packages"
    for path in files:
        if path.suffix not in {".csproj", ".props"}:
            continue
        for item in ET.parse(path).getroot().iter("PackageReference"):
            name, version = item.attrib["Include"], item.attrib.get("Version", "unresolved")
            packages.setdefault((name, version), []).append(path.parent.name)
    results = []
    for (name, version), projects in sorted(packages.items()):
        lib = cache / name.lower() / version / "lib"
        frameworks = sorted(p.name for p in lib.iterdir() if p.is_dir()) if lib.exists() else []
        if name == "Microsoft.NETFramework.ReferenceAssemblies":
            status = "build-only; net40 reference assets must be restored and verified"
        elif name == "System.Data.SQLite.Core":
            status = "native-validation-required; meta-package does not prove XP compatibility"
        elif not frameworks:
            status = "unknown; inspect assets and transitive dependencies"
        elif set(frameworks).intersection({"net20", "net35", "net40"}):
            status = "managed-framework-candidate; XP execution still required"
        else:
            status = "blocker; no cached net40-or-earlier managed asset"
        results.append(dict(package=name, version=version, projects=sorted(projects),
                            cached_lib_frameworks=frameworks, status=status))
    return results


def build_report(root, baseline):
    files = source_files(root)
    findings = scan(files, root)
    props = root / "Directory.Build.props"
    framework = ET.parse(props).findtext(".//TargetFramework")
    lock_paths = files + [props]
    for path in sorted(root.glob("*.sln")):
        lock_paths.append(path)
    fingerprint = {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in lock_paths}
    frozen_path = root / "baselines/windows-xp/source-lock.json"
    frozen = json.loads(frozen_path.read_text(encoding="utf-8"))["source_sha256"] if frozen_path.exists() else None
    drift = (dict(changed=sorted(p for p in fingerprint.keys() & frozen.keys() if fingerprint[p] != frozen[p]),
                  added=sorted(fingerprint.keys() - frozen.keys()), removed=sorted(frozen.keys() - fingerprint.keys()))
             if frozen is not None else None)
    return dict(schema_version=2, baseline=baseline["id"], based_on=baseline["based_on"],
                targets=baseline["targets"], all_targets_required_for_release=baseline["all_targets_required_for_release"],
                declared_stage=baseline["stage"], current_target_framework=framework,
                status="blocked", release_ready=False,
                limitations=["Lexical source scan: comments may match; aliases and other later APIs may be missed.",
                             "Cached package framework assets are observations, not complete native/transitive validation.",
                             "No XP runtime execution evidence has been validated; no XP package is authorized."],
                source_file_count=len(files), matched_line_count=len(findings), source_drift_from_b0=drift,
                matched_rules=sorted({x["rule"] for x in findings}), findings=findings,
                dependencies=dependencies(files + [props]),
                release_gates=[dict(target=t["id"], id=g, status="not-validated")
                               for t in baseline["targets"] for g in baseline["release_gates"]],
                source_sha256=fingerprint)


def markdown(report):
    rows = ["# Windows XP B0 兼容性审计", "", "> 状态：阻塞。此报告不是 XP 验收证明。", "",
            "当前目标框架：`" + report["current_target_framework"] + "`；源文件：" + str(report["source_file_count"]) +
            "；匹配行：" + str(report["matched_line_count"]) + "。", "",
            "仅扫描 src（排除 bin/obj）并读取项目依赖声明；不读取用户数据和模型凭据。", "",
            "B0 来源指纹差异：`" + json.dumps(report["source_drift_from_b0"], ensure_ascii=False) + "`。", "",
            "## 兼容性差异", "", "| 规则 | 领域 | 匹配行 | 首个位置 |", "|---|---|---:|---|"]
    for rule in report["matched_rules"]:
        matches = [x for x in report["findings"] if x["rule"] == rule]
        first = matches[0]
        rows.append(f'| {rule} | {first["area"]} | {len(matches)} | `{first["file"]}:{first["line"]}` |')
    rows += ["", "完整位置、处理方向与 SHA-256 指纹见同目录 audit.json。匹配包含声明和调用，不代表独立缺陷数。",
             "", "## 包资产观察", "", "| 包 | 版本 | 缓存 lib 目标 | 判断 |", "|---|---|---|---|"]
    for item in report["dependencies"]:
        rows.append(f'| {item["package"]} | {item["version"]} | {", ".join(item["cached_lib_frameworks"]) or "未发现/元包"} | {item["status"]} |')
    rows += ["", "## 发布门槛", ""]
    rows += ["- [ ] " + x["target"] + " / " + x["id"] for x in report["release_gates"]]
    rows += ["", "词法审计可能漏报新 API，也可能匹配注释。必须继续使用真实 net40 引用程序集编译、检查原生 DLL 导入，并在 XP 上运行验证。", ""]
    return "\n".join(rows)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Exit 2 while XP release remains blocked")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/diagnostics/windows-xp")
    args = parser.parse_args()
    output = args.output.resolve()
    # Keep generated diagnostics in this workspace even when invoked by other tools.
    output.relative_to(ROOT)
    baseline = json.loads(BASELINE.read_text(encoding="utf-8"))
    report = build_report(ROOT, baseline)
    output.mkdir(parents=True, exist_ok=True)
    (output / "audit.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    (output / "audit.md").write_text(markdown(report), encoding="utf-8")
    print(json.dumps(dict(baseline=report["baseline"], status=report["status"],
                          matched_rules=len(report["matched_rules"]), matched_lines=report["matched_line_count"],
                          source_files=report["source_file_count"], report=str(output / "audit.md")), ensure_ascii=False))
    return 2 if args.check else 0


if __name__ == "__main__":
    raise SystemExit(main())
