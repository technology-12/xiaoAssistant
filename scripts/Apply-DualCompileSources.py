"""Apply dual-compilable (net48 mainline + net40 XP) source accommodations.

Shared sources must compile in both the mainline (net48, real Task/HttpClient/
Regex APIs) and the XP line (net40, where WorkAssistant.Compatibility.Xp and
src/WorkAssistant.Core/XpRuntime.cs provide matching XpTask/XpArray helpers).
R10 fix: this script now matches the ACTUAL shipped API names (XpTask./XpArray.,
not the retired Xp.Tasks./Xp.Arrays. draft names) and covers every substitution
applied to the tree, in both directions.

Modes:
  apply     rewrite src/ and tests/ sources in place (forward, idempotent)
  check     report any remaining framework-4.5-only call forms (exit 1 if found)
  snapshot  apply to a COPY of the tree given by --target for replay validation
"""
import argparse
import re
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SKIP = {"XpRuntime.cs", "XpShims.cs", "XpRegex.cs", "XpCompression.cs", "PdfOcr.cs"}

# (pattern, replacement) — 正向：主线可读形态 → 双线可编译形态。
# 逆表由 same pairs 反推，供归因/回退实验使用（见交付报告 §4.1）。
FORWARD = [
    (re.compile(r"\bTask\.Run\("), "XpTask.Run("),
    (re.compile(r"\bTask\.WhenAll\("), "XpTask.WhenAll("),
    (re.compile(r"\bTask\.WhenAny\("), "XpTask.WhenAny("),
    (re.compile(r"\bTask\.FromResult\("), "XpTask.FromResult("),
    (re.compile(r"\bTask\.Delay\("), "XpTask.Delay("),
    (re.compile(r"\bArray\.Empty<"), "XpArray.Empty<"),
    (re.compile(r"Marshal\.SizeOf<(\w+)>\(\)"), r"Marshal.SizeOf(typeof(\1))"),
    (re.compile(r"new TaskCompletionSource<([^;{]+?)>\(TaskCreationOptions\.RunContinuationsAsynchronously\)"),
     r"XpTask.CompletionSource<\1>()"),
    (re.compile(r"new CancellationTokenSource\((TimeSpan\.[A-Za-z]+\([^)]*\)|AgentController\.TotalTimeout|\d+)\)"),
     r"XpTask.Cts(\1)"),
    (re.compile(r"Timeout\.InfiniteTimeSpan"), "TimeSpan.FromMilliseconds(-1)"),
    (re.compile(r"SecurityProtocolType\.Tls12"), "(SecurityProtocolType)3072"),
    (re.compile(r"\bIReadOnlyList<"), "IList<"),
    (re.compile(r"\bIReadOnlyCollection<"), "ICollection<"),
    (re.compile(r"\bIReadOnlyDictionary<"), "IDictionary<"),
    # 带超时正则（net40 无该重载）：显式列出已迁移调用点，避免误伤其它 3 参调用。
    (re.compile(r'Regex\.IsMatch\(([^,]+), (@[^,]+|"[^"]+"), RegexOptions\.IgnoreCase, TimeSpan\.FromMilliseconds\(50\)\)'),
     r"XpRegex.IsMatch(\1, \2, RegexOptions.IgnoreCase)"),
]

CHECK_RESIDUE = [
    r"\bTask\.(?:Run|WhenAll|WhenAny|FromResult|Delay)\s*\(",
    r"\bArray\.Empty<",
    r"\bIReadOnly(?:List|Collection|Dictionary)<",
    r"new TaskCompletionSource<[^>]+>\(TaskCreationOptions\.RunContinuationsAsynchronously\)",
    r"Marshal\.SizeOf<\w+>\(\)",
    r"Timeout\.InfiniteTimeSpan",
    r"SecurityProtocolType\.Tls12",
    r"Regex\.IsMatch\([^;]*TimeSpan\.FromMilliseconds\(50\)\)",
]


def sources(root):
    for folder in (root / "src", root / "tests"):
        for path in sorted(folder.rglob("*.cs")):
            if "bin" in path.parts or "obj" in path.parts or path.name in SKIP:
                continue
            yield path


def apply(root):
    total, touched = 0, []
    for path in sources(root):
        text = original = path.read_text(encoding="utf-8-sig")
        for pattern, replacement in FORWARD:
            text, count = pattern.subn(replacement, text)
            total += count
        if text != original:
            path.write_text(text, encoding="utf-8")
            touched.append(path.relative_to(root).as_posix())
    for name in touched:
        print(f"  {name}")
    print(f"forward substitutions: {total} in {len(touched)} files")


def check(root):
    residue = []
    for path in sources(root):
        text = path.read_text(encoding="utf-8-sig")
        for pattern in CHECK_RESIDUE:
            for match in re.finditer(pattern, text):
                line = text.count("\n", 0, match.start()) + 1
                residue.append(f"{path.relative_to(root).as_posix()}:{line}  {pattern}")
    for item in residue:
        print("RESIDUE:", item)
    print("residue count:", len(residue))
    return 1 if residue else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["apply", "check", "snapshot"])
    parser.add_argument("--target", type=Path, help="snapshot mode: copy tree here, then apply/check on the copy")
    args = parser.parse_args()
    root = ROOT
    if args.mode == "snapshot":
        if not args.target:
            raise SystemExit("snapshot mode requires --target")
        target = args.target.resolve()
        # S07：防误删边界——目标不得是仓库本体/仓库祖先/任何源目录；
        # 已存在的目录必须为空或是本脚本先前生成的快照（含标记文件），否则拒绝。
        repo_root = ROOT.resolve()
        guarded = {repo_root, repo_root / "src", repo_root / "tests"}
        for guard in guarded:
            if target == guard or guard in target.parents or target in guard.parents:
                raise SystemExit(f"refusing snapshot target inside/at the source tree: {target}")
        import tempfile
        system_temp = Path(tempfile.gettempdir()).resolve()
        if system_temp not in target.parents and target != system_temp:
            raise SystemExit(f"snapshot target must live under the system temp dir ({system_temp}), got: {target}")
        marker = target / ".xp-dualcompile-snapshot"
        if target.exists():
            if not marker.is_file():
                raise SystemExit(f"refusing to overwrite non-snapshot directory: {target}")
            shutil.rmtree(target)
        target.mkdir(parents=True)
        marker.write_text("generated by Apply-DualCompileSources.py snapshot\n", encoding="ascii")
        for folder in ("src", "tests"):
            shutil.copytree(ROOT / folder, target / folder,
                            ignore=shutil.ignore_patterns("bin", "obj"))
        root = target
        print("snapshot at", root)
    if args.mode in ("apply", "snapshot"):
        apply(root)
    return check(root)


if __name__ == "__main__":
    raise SystemExit(main())
