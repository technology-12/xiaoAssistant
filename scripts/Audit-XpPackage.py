# -*- coding: utf-8 -*-
"""XP 候选包 PE/依赖审计：为 XP-B 基线发布门槛提供构建证据。

只输出结构性信息（机器类型、子系统版本、托管标记、原生导入表），
不读取源代码、凭据或用户数据。这不是 XP 实机验收。
"""
import argparse
import hashlib
import json
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
NUL = bytes([0])
PE_SIGNATURE = b"PE" + NUL + NUL

# 词法级 Vista+ 导入风险提示（非完整 API 清单）
VISTA_PLUS_IMPORTS = {
    "getfinalpathnamebyhandle", "setfileinformationbyhandle",
    "getnamedpipeclientprocessid",
    "initializeconditionvariable", "wakeconditionvariable",
    "sleepconditionvariablecs", "getsystemtimepreciseasfiletime",
    "setthreadgroupaffinity", "getlogicalprocessorinformationex",
    "tryacquiresrwlockexclusive", "initializesrwlock",
}


def read_cstring(data, offset):
    end = data.find(NUL, offset)
    if end < 0:
        return ""
    return data[offset:end].decode("ascii", "replace")


def parse_pe(path):
    data = path.read_bytes()
    if len(data) < 0x40 or data[:2] != b"MZ":
        return None
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if pe <= 0 or pe + 24 > len(data) or data[pe:pe + 4] != PE_SIGNATURE:
        return None
    machine = struct.unpack_from("<H", data, pe + 4)[0]
    sections = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    machines = {0x14C: "x86", 0x8664: "x64", 0x1C0: "arm", 0xAA64: "arm64"}
    optional = pe + 24
    if opt_size < 64 or optional + opt_size > len(data):
        return None
    magic = struct.unpack_from("<H", data, optional)[0]
    pe32plus = magic == 0x20B
    subsystem = struct.unpack_from("<H", data, optional + 68)[0]
    sub_major, sub_minor = struct.unpack_from("<HH", data, optional + 48)
    directories = optional + (112 if pe32plus else 96)
    if directories + 16 * 8 > len(data):
        return None
    cli_rva = struct.unpack_from("<I", data, directories + 14 * 8)[0]
    import_rva = struct.unpack_from("<I", data, directories + 8)[0]

    sections_raw = []
    table = optional + opt_size
    for index in range(min(sections, 96)):
        base = table + index * 40
        if base + 40 > len(data):
            break
        vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", data, base + 8)
        sections_raw.append((vaddr, vsize, rawptr, rawsize))

    def rva_to_offset(rva):
        for vaddr, vsize, rawptr, rawsize in sections_raw:
            if vaddr <= rva < vaddr + max(vsize, rawsize or 1):
                return rawptr + (rva - vaddr)
        return None

    managed = cli_rva != 0
    imports = {}
    if import_rva:
        import_off = rva_to_offset(import_rva)
        if import_off is not None:
            position = import_off
            while position + 20 <= len(data):
                original, _, _, name_rva, thunk = struct.unpack_from("<IIIII", data, position)
                if name_rva == 0 and thunk == 0:
                    break
                name_off = rva_to_offset(name_rva) if name_rva else None
                dll = read_cstring(data, name_off).lower() if name_off is not None else "?"
                functions = []
                thunk_rva = thunk or original
                thunk_off = rva_to_offset(thunk_rva) if thunk_rva else None
                if thunk_off is not None:
                    cursor = thunk_off
                    step = 8 if pe32plus else 4
                    ordinal_flag = 0x8000000000000000 if pe32plus else 0x80000000
                    while cursor + step <= len(data):
                        entry = struct.unpack_from("<Q" if pe32plus else "<I", data, cursor)[0]
                        if entry == 0:
                            break
                        if not entry & ordinal_flag:
                            hint_off = rva_to_offset(entry)
                            if hint_off is not None and hint_off + 2 < len(data):
                                functions.append(read_cstring(data, hint_off + 2).lower())
                        cursor += step
                imports[dll] = functions
                position += 20
    return {
        "file": path.name,
        "machine": machines.get(machine, hex(machine)),
        "subsystem": subsystem,
        "subsystem_version": f"{sub_major}.{sub_minor}",
        "managed": managed,
        "imports": imports,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", type=Path, default=ROOT / "artifacts/xp-candidate/Release")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/diagnostics/windows-xp")
    args = parser.parse_args()
    report = {"schema_version": 1, "generated_by": "Audit-XpPackage.py", "targets": {}}
    problems = []
    required_payload = [
        "WorkAssistant.Desktop.exe", "WorkAssistant.Host.exe", "Xiao.Launcher.exe",
        "WorkAssistant.Core.dll", "WorkAssistant.Infrastructure.dll", "WorkAssistant.Tools.dll",
        "WorkAssistant.OpenCode.dll", "WorkAssistant.Compatibility.Xp.dll",
        "Newtonsoft.Json.dll", "System.Data.SQLite.dll", "System.ValueTuple.dll",
        "app.defaults.json", "kernel.policy.json",
    ]
    for arch, subsystem_expect, machine_expect in (("x86", 5.1, "x86"), ("x64", 5.2, "x64")):
        folder = args.candidate / arch
        if not folder.is_dir():
            problems.append(f"target platform directory missing: {arch}（candidate 不完整，审计失败）")
            continue
        for required in required_payload:
            if not (folder / required).is_file():
                problems.append(f"{arch}: 必需载荷缺失 {required}")
        if not (folder / arch / "SQLite.Interop.dll").is_file():
            problems.append(f"{arch}/{arch}/SQLite.Interop.dll 缺失（原生依赖未配套）")
        binaries = []
        interop_findings = []
        for path in sorted(list(folder.rglob("*.dll")) + list(folder.rglob("*.exe"))):
            if any(part in {"testrun", "testrun-x64"} for part in path.parts):
                continue
            required = path.name in required_payload or ("SQLite.Interop" in path.name)
            try:
                info = parse_pe(path)
            except Exception as error:  # 结构异常按问题记录，不让审计中断
                problems.append(f"{arch}/{path.name}: PE 解析失败（{type(error).__name__}）")
                continue
            if info is None:
                # S04：声明分发的 EXE/DLL 不可解析（如损坏/非 PE）必须失败，不得静默跳过。
                problems.append(f"{arch}/{path.name}: 不是可解析的 PE 文件（损坏或非 Windows 可执行）")
                continue
            binaries.append({
                "file": info["file"], "machine": info["machine"], "managed": info["managed"],
                "subsystem": info["subsystem"], "subsystem_version": info["subsystem_version"],
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            })
            if not info["managed"] and info["machine"] != machine_expect:
                problems.append(f"{arch}/{path.relative_to(folder).as_posix()}: native machine={info['machine']} expected {machine_expect}")
            if "SQLite.Interop" in path.name:
                flagged = sorted({fn for funcs in info["imports"].values() for fn in funcs if fn in VISTA_PLUS_IMPORTS})
                crt = sorted(d for d in info["imports"] if any(k in d for k in ("msvcr", "ucrtbase", "vcruntime", "msvcp")))
                interop_findings.append({
                    "file": path.relative_to(folder).as_posix(),
                    "flagged_vista_plus_imports": flagged,
                    "crt_dependencies": crt,
                    "import_dlls": sorted(info["imports"]),
                })
                if flagged:
                    problems.append(f"{arch}/{path.name}: SQLite.Interop 含 Vista+ 导入 {flagged}")
                if crt:
                    problems.append(f"{arch}/{path.name}: SQLite.Interop 依赖动态 CRT {crt}（XP 需额外运行库）")
            if path.suffix.lower() == ".exe":
                if info["machine"] != machine_expect:
                    problems.append(f"{arch}/{path.name}: machine={info['machine']} 期望 {machine_expect}")
                if abs(float(info["subsystem_version"]) - subsystem_expect) > 0.001:
                    problems.append(f"{arch}/{path.name}: subsystem={info['subsystem_version']} expected {subsystem_expect:.2f}")
            if not info["managed"] and path.suffix.lower() == ".dll" and "SQLite.Interop" not in path.name:
                problems.append(f"{arch}/{path.name}: 非托管 DLL 且不在白名单")
        report["targets"][arch] = {
            "expected": {"machine": machine_expect, "pe_subsystem_version": f"{subsystem_expect:.2f}"},
            "binaries": binaries,
            "sqlite_interop": interop_findings,
        }
    for arch, target in report["targets"].items():
        if not target.get("binaries"):
            problems.append(f"{arch}: 未发现任何可解析二进制（candidate 为空或不可读，审计失败）")
    report["structural_problems"] = problems
    args.output.mkdir(parents=True, exist_ok=True)
    (args.output / "xp-package-pe-audit.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    summary = {
        "x86_binaries": len(report["targets"].get("x86", {}).get("binaries", [])),
        "x64_binaries": len(report["targets"].get("x64", {}).get("binaries", [])),
        "structural_problems": problems,
        "sqlite_interop": {arch: target.get("sqlite_interop") for arch, target in report["targets"].items()},
    }
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
