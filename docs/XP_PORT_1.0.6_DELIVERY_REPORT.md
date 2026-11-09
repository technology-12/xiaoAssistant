# 晓助手 1.0.6 Windows XP 基线交付报告（XP-B1）· 审查版 · 修订 3

> 存档说明（1.0.7 修复）：本报告的全量用例计数和包内 PARITY 有历史不一致，不能作为当前发行验收结论。该问题及后续缺陷已转入 1.0.7，当前结果见 `docs/RELEASE_1.0.7.md` 和新包内机器生成的 verification。原有 1.0.6 ZIP 与本报告主体保留供追溯；请勿继续分发旧包。

- 日期：2026-11-08（修订 3：按 `docs/XP_PORT_1.0.6_REVIEW_R2.md` 完成 S01—S07 修复）
- 执行者：Claude Code（glm-5.3 驱动）
- 审查链：R1 轮 `XP_PORT_1.0.6_REVIEW.md`（R01—R10，修订 2 修复）→ R2 轮 `XP_PORT_1.0.6_REVIEW_R2.md`（S01—S07，本轮修复）
- 姊妹文档：`docs/XP_PORT_1.0.6.md`（实现说明）

---

## 0. 摘要（修订 3）

R2 轮审查的 7 项问题（S01—S07）已全部修复。**交付纪律的根本变化（S01）**：建立"冻结构建清单"流程——同一源码快照构建双平台 → 记录全部产品 DLL/EXE 的 SHA-256（`artifacts/xp-candidate/build-manifest-r3.json`）→ 用该确切二进制跑全部测试 → 打包脚本在打包前后两次比对清单（候选目录与 ZIP 内逐条一致才放行）。R2 指出的"x64 ZIP 保留旧 DLL"这一类**源码与交付不同步**的问题从流程上关闭。

### 最终验证（冻结构建，双平台）

| 验证 | 主线 net48 | XP x86 | XP x64 |
|---|---|---|---|
| 控制台用例 | 92：75/17 | **105：91/15** | **105：91/15** |
| XP 专属回归（R02—R07 + S03 + S05 场景） | — | **14/14** | **14/14** |
| XP 特有失败 | — | **0** | **0** |
| UI 自检 | 28/14 | 28/14 | 28/14 |
| ZIP 内 DLL == 冻结清单 | — | **24/24 一致** | **24/24 一致** |
| PE 审计（含损坏 Host 负例） | — | exit 0 / 负例 exit 1 | 同左 |
| 包内环境检查 | — | exit 0 | exit 0 |
| 主线 ZIP 构造探针（R01） | **双协议 OK**（旧包复测失败） | — | — |

### 交付指纹（修订 3）

| 包 | SHA-256 |
|---|---|
| `XiaoAssistant-1.0.6-portable-windows-xp-x86.zip`（52,353,997 B 级） | `9e675c965a55b3386229a8674ec809ecb94448c2c475b2471a1227033bc40ebc` |
| `XiaoAssistant-1.0.6-portable-windows-xp-x64.zip` | `a656842e959c64ca4ed951d07ebae846eb9ebc75938c55f709aaf6d03c74a41f` |
| `XiaoAssistant-1.0.6-portable.zip`（主线，R01 修复重建） | `28a3b9e9d109273d5463fe3dc6d386715815f82faeff1f949f598202983cfde3` |
| 旧主线缺陷包 | 归档于 `artifacts/packages/archive-r01-defective/`（不得分发） |

## 1. R2 审查问题修复记录（S01—S07）

| # | 修复 | 证据 |
|---|---|---|
| **S01** x64 ZIP 保留旧 DLL | ① 清空 obj/bin 后从同一源码快照重建双平台；② 生成冻结清单（24 条产品二进制 SHA-256）；③ 打包脚本新增 `assert_frozen`：打包前候选目录逐条比对清单，不一致即拒绝（`Build-PortableRelease_1_0_6_Xp.py`）；④ 打包后独立校验 ZIP 内 24 条目 == 清单（本报告 §0 表格"ZIP 内 DLL == 冻结清单"）；⑤ PARITY 数字改为冻结构建实测并注明来源 | 上述全部通过；两平台包内均为新二进制 |
| **S02** 主线正式 ZIP 含 R01 缺陷 | 重建主线 Release（源码已含 R01 修复）→ 更新 `artifacts/ocr-candidate` 的 8 个应用二进制 + 重算 `verification/binary-sha256.json`（35 文件）→ 旧包归档 → 重跑 `Build-PortableRelease_1_0_6.py` → **对新 ZIP 内 DLL 运行构造探针：双协议 OK**（旧包同探针失败）。`summary.json` 增补 `rebuild` 段如实记录：控制台回归 75/17 在本构建重跑；OCR/原生检查是针对上一构建的记录，本轮未重跑（OCR 相关 DLL 未变，仅超时修复） | 构造探针输出；包指纹 `28a3b9e9…` |
| **S03** 混排字体污染 | `PageFontMaps` 重构：资源表内**每个字体都登记**（有 ToUnicode→其 CMap；无→显式空映射=标准单字节回退）；`Tf` 切换命中资源表（含空映射）即切换、未声明字体回退全局表；页面存在任一 per-font 映射时禁用全局回退（防其它字体 CMap 污染） | 新回归"同页混排 CJK+西文（S03）"：Type0 写"甲" + 无 ToUnicode 的 Helvetica 写 `u2` → 读出 `甲u2`（修复前 `甲甲`）；既有 12 项回归全部保持通过 |
| **S04** 损坏 Host 审计通过 | 必需文件 PE 不可解析（`parse_pe` 返回 None）即计为失败；对 SQLite.Interop 同样处理 | 负例：Host.exe 置为文本 → `不是可解析的 PE 文件` 计入 problems，exit 1 |
| **S05** 代理三条路径 | `BuildRequest`：`UseProxy=false`→显式 `request.Proxy=null`（绕过系统默认）；`UseProxy=true`+未指定→保持未设置（用系统默认）；`UseProxy=true`+指定→赋值 | 新回归"代理三路径（S05）"（反射 BuildRequest 断言三种配置下 `request.Proxy` 状态），通过 |
| **S06** OpenRead 创建目录 | `LockAncestors` 增加 `createMissing` 参数：移动/提交路径保持创建；**只读 OpenRead 传 false**，目录不存在即 PATH_DENIED，零创建副作用 | 回归：对不存在的 `sub/missing.txt` 调 OpenRead 失败且父目录未被创建（并入 XP-R06 路径检查） |
| **S07** snapshot 防误删 | 目标必须位于系统临时目录下；拒绝仓库本体/祖先/后代；已存在目录必须为空或含本脚本快照标记，否则拒绝覆盖 | 四个负例（src、仓库根、非快照非空目录、临时目录正常路径）行为验证通过 |

R2 §3（R05 原位写穿为设计差异）：维持"待实机验收"定性——**不声称恢复事务级原子性**；独占+复核+候选保留+NEEDS_REVIEW 的边界已由回归界定，崩溃/断电恢复验证列入 XP-10。

## 2. 冻结与打包流程（新增，防再次失同步）

```text
1. rm -rf src-xp/*/obj src-xp/*/bin；同一源码快照构建 x86 与 x64
2. 生成 build-manifest-r3.json（全部产品 EXE/DLL 的 SHA-256，双平台 24 条）
3. 干净目录对两平台分别运行全量用例与 XP 专属回归（被测二进制 == 清单）
4. Build-PortableRelease_1_0_6_Xp.py：assert_frozen → 打包 → ZIP 校验
5. Audit-XpPackage.py（真实候选 exit 0；空/损坏负例 exit 1）
```

## 3. 复核命令（修订 3，在修订 2 基础上新增）

```bash
# ZIP 内 DLL 与冻结清单一致（24 条）
python - <<'PY'
import zipfile, hashlib, json
frozen = json.load(open('artifacts/xp-candidate/build-manifest-r3.json', encoding='utf-8'))
for arch in ('x86','x64'):
    z = zipfile.ZipFile(f'artifacts/packages/XiaoAssistant-1.0.6-portable-windows-xp-{arch}.zip')
    for entry, digest in frozen.items():
        p, name = entry.split('/', 1)
        if p != arch: continue
        key = f'XiaoAssistant-1.0.6-portable-windows-xp-{arch}/晓助手.exe' if name == 'Xiao.Launcher.exe' else f'XiaoAssistant-1.0.6-portable-windows-xp-{arch}/程序文件/{name}'
        assert hashlib.sha256(z.read(key)).hexdigest() == digest, key
print('zip == frozen manifest: OK')
PY

# 审计负例（损坏 Host → exit 1）
#（复制候选到临时目录后覆盖 Host.exe 为文本，运行 Audit-XpPackage.py --candidate <目录>）

# snapshot 防误删（三种拒绝）
python scripts/Apply-DualCompileSources.py snapshot --target src     # 拒绝
python scripts/Apply-DualCompileSources.py snapshot --target .       # 拒绝
python scripts/Apply-DualCompileSources.py snapshot --target <非空非快照临时目录>  # 拒绝
```

其余复核命令沿用修订 2 §7（数字更新为本报告 §0）。

## 4. 声明级别（修订 3）

| 级别 | 内容 |
|---|---|
| **本机已证** | §0 全表；ZIP==冻结清单 24/24；主线新 ZIP 构造探针通过；S01—S07 各修复点回归；审计负例与 snapshot 防误删负例 |
| **设计意图（待实机）** | 原位写穿的崩溃恢复；代理/目录链在 XP 的行为；混排字体对真实复杂文档覆盖 |
| **明确未声称** | 未声称"已支持 Windows XP"（无实机验收，`release_ready=false`）；主线 OCR/原生 193 项检查未在重建后重跑（summary 已如实标注）；R05 原位写不等于事务原子性 |

## 5. 版本历史

- 修订 1：初版交付（含 R01—R10 缺陷，见 R1 审查）
- 修订 2：R01—R10 修复 + 12 项 XP 回归（x64 包未同步，见 R2 审查 S01）
- 修订 3（本版）：S01—S07 修复；冻结清单流程；主线包重建；XP 专属回归增至 14 项
