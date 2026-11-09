# XP 1.0.6 修订 3 独立复审

日期：2026-11-08。对象：交付报告修订 3、当前源码、x86/x64 XP ZIP，以及重新交付的主线 1.0.6 ZIP。

结论：**上一轮主要故障和交付包失同步问题已修复，仍不满足正式 XP 发布验收。** 本轮发现一个可复现的 PDF 功能缺陷、一个发布审计门禁漏洞，以及交付证据未同步的问题。不能把 14 项专属回归通过解释为全部 PDF 场景或真实 XP 已通过。

本轮只新增审查文档和隔离诊断产物，没有修改产品代码、交付报告或发行包。源码构建更新了常规 obj 缓存；独立输出位于 `artifacts/diagnostics/xp-review-r3/`。

## 1. 已确认的修复

| 上轮项目 | 本轮复核结果 |
|---|---|
| S01：x64 包仍含旧 DLL | 两个 ZIP 每平台 12 个产品二进制均匹配冻结清单，共 24/24；用于测试的解压副本也逐一匹配。此项核心交付问题已关闭。测试证据数字另见 T03。 |
| S02：主线 ZIP 仍含错误无限超时 | 新 ZIP 指纹与报告一致；被测 Infrastructure.dll 与 ZIP 内字节一致。OpenAI Chat、Anthropic Messages 构造探针均成功，未调用外部 API。 |
| S03：同页 CJK/西文字体污染 | XP 两平台实际包均读取为“甲u2”；专属混排回归也通过。 |
| S04：损坏 Host 仍通过审计 | 完整隔离候选 exit 0；仅将隔离副本 Host 改为文本后，审计 exit 1。原损坏文件问题已修复，原生 DLL 位数另见 T02。 |
| S05：代理被清除 | 独立探针确认保留系统代理；内置代理三路径回归双平台通过。 |
| S06：只读打开创建目录 | 不存在路径读取失败后未创建父目录，双平台均确认。 |
| S07：snapshot 误删边界 | 源码已增加仓库本体/祖先/后代拒绝、临时目录范围和快照标记检查；实际以 src 为目标时在删除前拒绝。注意当前实现也拒绝已存在的空目录，与报告“为空或有标记”的描述略有不同，未视为功能阻断。 |

两个平台的源码分别构建成功。现有 14 项 XP 专属测试均对匹配冻结清单的产品 DLL 运行：每平台 13 项在沙箱内通过；HTTP.sys 用例在沙箱内报 PlatformNotSupportedException，在沙箱外仅运行隔离的本机伪服务后通过。合计各 14 个不同用例通过，此环境差异不计为产品缺陷。

独立探针还确认：WhenAll 等待兄弟任务；父子上下文隔离；async OCE 为 Canceled；管道/HTTP 取消后能终结；两页 PDF 恰好两页；并发写者被拒绝；目录联接越界被拒绝；XP 工具不再宣称具备 OCR。HTTP 中断当前仍以 Faulted 终结，不等价于原生取消状态。

## 2. 新发现与遗留

### T01 [P1] 合法的增量保存 PDF 触发重复键异常

位置：`src-xp/WorkAssistant.Tools/PdfText.Xp.cs:97–98`，对象扫描位于 `:173`。

复现文件：`artifacts/diagnostics/xp-review-r3/fixtures/incremental.pdf`。从已通过混排测试的 PDF 出发，以 pypdf 的 incremental 模式仅更新文档 Title，保留原文档并追加更新对象。这是正常的增量保存结构，并非损坏 PDF。

- pypdf 对照提取结果：`甲\nu2\n`。
- 当前主线 1.0.6 实际包：正常返回 `甲u2`。
- 当前 XP x86 和 x64 实际包：均抛 `System.ArgumentException`，消息“已添加了具有相同键的项。”

原因：ScanObjects 将文件中的所有对象版本加入列表，随后直接按对象号 ToDictionary。增量 PDF 允许相同对象号的新版本追加到文件，触发重复键，尚未进入页面读取逻辑就失败。普通编辑、添加批注等保存流程可能产生此类文档。

修复要求：按 PDF 交叉引用及增量更新规则解析当前有效对象，正确处理对象代数、更新、释放及页内容引用；至少覆盖元数据更新和实际页面文字更新的增量样例。不要仅吞掉异常或无条件保留第一个对象，也不要仅修 byNumber 而让 DecodeStreams 继续使用旧对象版本。

证据：`pdf-incremental-x86.txt`、`pdf-incremental-x64.txt`、`pdf-incremental-mainline.txt`、`incremental-result.json`。可用 `PdfProbe-x86.exe <XP-x86程序文件目录> <incremental.pdf>` 独立复现；x64 使用对应探针。

### T02 [P2] PE 发布审计不验证 SQLite 原生 DLL 的机器类型

位置：`scripts/Audit-XpPackage.py:159–176`。机器类型比较只在 `.exe` 分支中执行；SQLite.Interop 虽做导入/CRT 检查，却没有位数比较。

在本轮隔离候选副本中，将 `x64/x64/SQLite.Interop.dll` 替换为 x86 版本，保持文件名和目录不变：审计仍 **exit 0，structural_problems=[]**。审计 JSON 本身记录该文件为 x86，但未判为失败。

影响：错误位数的 SQLite 原生库会在 x64 进程加载时失败，现有发布门禁却放行。**当前实际 ZIP 的原生 DLL 位数正确**，本项不是认定当前包已混入错误 DLL；冻结散列校验也不能替代对首次生成清单时的架构验证。

修复要求：对各平台实际加载的原生 DLL 校验机器类型，并增加双向错配负例；继续允许设计上合法的托管 AnyCPU 依赖，避免把所有托管 DLL 简单按 PE machine 一刀切。

证据：`audit-wrong-native-arch/xp-package-pe-audit.json`、`audit-wrong-native-arch.txt`。对照 `audit-valid` 为 exit 0，`audit-corrupt-host` 为 exit 1。

### T03 [P2] 包内测试证据与报告仍不一致

位置：`scripts/Build-PortableRelease_1_0_6_Xp.py:37–44`，两个 ZIP 内的 `程序文件/verification/parity-evidence.json`，交付报告 §0。

- 报告写专属回归 14/14；两个 ZIP 和脚本仍硬编码 13/13。本轮实际运行确认为 14 项。
- 报告控制台写“105：91/15”，但 91+15=106；包内则为总计 105、通过 90、失败 15。
- 因此报告声称“PARITY 数字改为冻结构建实测”尚不能成立。本轮没有重跑全部控制台及 UI 用例，不将任一组全量数字认定为正确值。

修复要求：从同一冻结构建的机器可读测试结果生成包内 evidence 和报告，校验总数=通过+失败+跳过，并绑定 build id/二进制清单；删除手写的结果常量。

## 3. 仍需验收的边界

- 本轮环境为 Windows 10.0.26200 / CLR 4.8；net40 编译及本机运行不等于 XP 上可运行。Windows XP SP3 x86、XP Professional x64 SP2 的启动、网络/TLS、代理、SQLite、文档处理等真实环境证据仍缺失，继续保持 release_ready=false。
- XP 仍无本地 OCR，不能继承主线 OCR 能力的验收结论。本轮未重跑主线 OCR 完整测试。
- R05 原位写穿仍是已披露的设计差异：已有独占写、候选文件和上层持久备份/日志，不能说没有恢复措施；但中断/断电/磁盘满时的恢复还需专项验证，不能称作事务级原子替换。
- 报告列出的共享控制台/UI 失败也尚未归零；本轮不替此前全量测试结果背书。

## 4. 本轮包指纹及证据

| 包 | SHA-256 |
|---|---|
| XP x86 | `9e675c965a55b3386229a8674ec809ecb94448c2c475b2471a1227033bc40ebc` |
| XP x64 | `a656842e959c64ca4ed951d07ebae846eb9ebc75938c55f709aaf6d03c74a41f` |
| 主线 1.0.6 | `28a3b9e9d109273d5463fe3dc6d386715815f82faeff1f949f598202983cfde3` |

诊断目录：`artifacts/diagnostics/xp-review-r3/`。其中 `frozen-package-check.json` 记录两个 XP 包各 12 项散列一致；`package-x86.txt` / `package-x64.txt` 为独立行为探针；`xp-regressions-*.txt` 和 `xp-http-unsandboxed-*.txt` 为回归日志；`extend_review.py` 与 `PdfProbe.cs` 为可复用的隔离负例/提取探针源码。诊断目录的错误架构副本仅用于审计负例，禁止作为候选发行包使用。
