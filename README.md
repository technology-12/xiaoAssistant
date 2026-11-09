# 晓助手 1.0.7 源码与技术方案

<!-- windows-uos-baselines -->
## Windows 与统信 UOS 基线

- [双基线管理入口](baselines/README.md)：Windows 1.0.7 源码基线与 UOS B0 规划基线分开管理。
- [统信迁移文档](docs/linux-port/README.md)：迁移方案、开发环境评估、21 个 AI 任务与验收条件。
- macOS、Windows 均可开发统信版；目标系统验证在 UOS/Linux 进行。UOS 当前尚无已验收发布物。


当前基线包含 Windows 主线和 XP 试用候选源码。版本修复和验证范围见 [1.0.7 交付报告](docs/RELEASE_1.0.7.md)。

从 Mac 开发 Linux 桌面版本，请先读 [Linux 移植交接](docs/LINUX_PORT_HANDOFF.md)。真实模型凭据在应用内配置，仓库的默认 API 密钥为空。

以下为历史设计与开发记录，其中的旧版构建产物路径用于追溯。

# 晓助手（Windows 桌面办公助手）

> 第三轮适配（2026-11-04）：新增原生 Anthropic Messages、跨协议子 Agent、模型窗口与输出预留、能力缓存和本地 OpenCode 风格模型目录。见 [第三轮交接](docs/OPENCODE_ADAPTATION_ITERATION3_2026-11-04.md) 与 [全量对齐矩阵](docs/OPENCODE_FULL_PARITY_MATRIX_2026-11-04.md)。

> 第二轮适配：已补齐模型重试与超时提示、父子任务写入中断复核、成果重复导入复用。新版入口为 artifacts/iteration3-candidate/WorkAssistant.Desktop.exe，详见 [第二轮交接](docs/OPENCODE_ADAPTATION_ITERATION2_2026-11-03.md) 与 [迭代记录](docs/OPENCODE_ADAPTATION_ITERATION_LOG.md)。

> 最新内核改造：已加入子任务依赖、成果交接和工作区 allow/ask/deny 权限，见 [实现与验证说明](docs/OPENCODE_NATIVE_COLLABORATION_2026-11-03.md)。

> 2026-11-03：新增本地并发子 Agent（最多 2 个并发、每轮 4 个），支持独立模型 API Profile。使用与实现见 [本地子 Agent 交接](docs/LOCAL_SUBAGENTS_2026-11-03.md)。

> 当前版本：`artifacts/iteration3-candidate/WorkAssistant.Desktop.exe`。采用本机 C#/.NET 4.8 增强内核，直接连接模型服务；无需 Node.js、OpenCode 进程或独立网关。新增自动上下文摘要、原始记录回查、任务计划、文档阅读/搜索和按需技能。详见 [本机内核增强与交接](docs/NATIVE_KERNEL_ENHANCEMENT_2026-11-03.md)。运行前请完全退出旧版及托盘实例。

> 先前 OpenCode 远程迁移版保留在 `artifacts/opencode-candidate/`，设计和服务代码保留供参考；它不是当前默认运行路线。

> 保留的自研内核候选：`artifacts/agent-candidate/`，其架构改进详见 [研究与实现](docs/AGENT_ARCHITECTURE_RESEARCH_AND_IMPLEMENTATION.md)。可通过新客户端设置关闭远程内核回退。

> 左侧项目管理与对话归类仍然保留；删除项目保留文件及任务记录。详见 [项目与对话操作说明](docs/PROJECTS_AND_CONVERSATIONS.md)。此前项目版位于 `artifacts/projects-candidate/`，旧 `release/` 不是最新构建。

> 2026-11-01：源码已按审查补充非测试工作包，当前候选入口为 `artifacts/production-candidate/WorkAssistant.Desktop.exe`。旧 `release/` 保留原版本。详见 [逐包修复记录](docs/PRODUCTION_FIXES_2026-11-01.md)。本轮仅完成构建，未运行测试；正式签名安装包尚需发布证书及打包工具。

> 最新增量：[技能工作室 / Skill Creator / GUI V4 / 图标](docs/SKILLS_AND_GUI_V4_2026-11-01.md)。销售汇总快捷入口已移除；新增 5 个应用内技能。代码行数见 [统计报告](docs/SOURCE_LINES.md)。

界面为 V3「留白工作室」，本版为面向正式版 1.0（[PRODUCTION_IMPLEMENTATION_PLAN.md](docs/PRODUCTION_IMPLEMENTATION_PLAN.md)）
的第一个完整功能增量：在 Demo 的 CSV 汇总闭环之上，补齐了**连续对话、问答任务、XLSX/DOCX/PDF 办公能力、
文件整理（计划→确认→执行）、SSE 流式输出、多模型 Profile 与任务队列**。当前入口见上方本机增强版说明。

设计依据：[Demo 实现设计](docs/DEMO_IMPLEMENTATION_DESIGN.md) ·
[GUI 改版规格](docs/GUI_REDESIGN_SPEC.md) · [1.0 实施进度](docs/PRODUCTION_PROGRESS.md) ·
[GUI V3 验证](docs/GUI_V3_VALIDATION.md)。

## 快速开始（最终用户）

前置要求：**.NET Framework 4.8**（Win11/Win10 自带；Win7 SP1 需安装，
见 [系统要求](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements)）。

1. 打开 `artifacts/iteration3-candidate/WorkAssistant.Desktop.exe`，使用完整目录。
2. 欢迎页点「选择工作区」授权一个文件夹（程序会自动创建 `inputs/` 与 `outputs/`）。
3. 把 `samples/workspace/inputs/` 下的示例（两个 CSV、`sales.xlsx`、`产品说明.pdf`）复制到 `inputs/`。
4. 保持默认 **演示模式**（不访问网络，脚本化走完各工具闭环），点快捷任务或直接输入，
   `Ctrl+Enter` 发送、`Enter` 换行。

### 演示模式可试的完整闭环

| 说法 | 走的管线 | 交付 |
|---|---|---|
| 汇总 inputs 中的销售 CSV… | list → aggregate → export_summary | summary.csv + report.md（合计 371.00） |
| 只保留销售一部，重新汇总 | 同会话第 2 轮，带筛选口径重新计算 | 新输出目录的 summary.csv + report.md（150.40） |
| 这两个文件是什么格式？ | 纯问答轮（不生成文件也判成功） | 文本回复 |
| 汇总 inputs 中的销售 Excel… | list(xlsx) → aggregate_table → export_xlsx | summary.xlsx（Excel 数值单元格） |
| 把销售 CSV 汇总生成 Word 报告 | aggregate_table → generate_docx | report.docx（口径/表格/校验说明） |
| 提取 inputs 中 PDF 的文字 | list(pdf) → extract_pdf_text | 带页码的 Markdown（含中文 CMap PDF） |
| 把 inputs 里的文件整理归档 | plan_file_organization → **用户确认** → apply_file_plan | organize_plan.md + file_mutations.md（只移动，不删除） |

### 连续对话

同一个会话内可以多轮追问：「只保留华东地区」这类后续轮次会引用此前口径重新计算，
成果写入**新的输出目录**，不覆盖上一版（正式版 §5.4）。问答轮不会强制生成文件（§4.1）。
任务运行中 `Ctrl+Enter` 可把新请求加入队列，串行执行（本地写工具不并发）。

### 文件整理与确认

`plan_file_organization` 只生成计划；`apply_file_plan` 执行前弹出**审批对话框**，
逐项列出“源 → 目标”，你同意才执行（演示模式自动通过并在步骤中可见）。
只允许移动/改名、不允许删除；任何一项失败即停止并进入“待人工复核”状态（NEEDS_REVIEW），不自动重试。

### 接真实模型

侧栏「模型与偏好设置」：支持**多个 Profile**（公网 / 指定 IP 网关），可新增、删除、切换；
API Key 按 Profile 用 Windows DPAPI（当前用户）加密保存在
`%LOCALAPPDATA%\Win7WorkAssistant\credentials\`。测试连接分级报告认证 / 模型 / 工具调用；
在线模式默认 **SSE 流式**输出（端点不支持时自动回退非流式）。

## 工具清单（内置，1.0）

| 工具 | 权限 | 说明 |
|---|---|---|
| `list_files` | 只读 | 按 extensions（csv/xlsx/docx/pdf）列 inputs 文件 |
| `preview_csv` / `preview_table` | 只读 | 表头 + 预览行（CSV 与 XLSX 同管线） |
| `aggregate_csv` / `aggregate_table` | 只读 | 分组求和（decimal 精确）；table 支持 where 筛选与 group_mapping 映射，口径写入成果 |
| `export_summary` | 写 outputs | summary.csv + report.md（含口径章节） |
| `export_xlsx` | 写 outputs | Excel 汇总表，数值为原生单元格，导出后重新解析核验 |
| `generate_docx` | 写 outputs | 受控文档模型（标题/段落/表格），数字来自聚合结果，不接受模型 XML |
| `preview_docx_fields` / `fill_docx_template` | 读 / 写 outputs | `{{字段}}` 模板填充；缺字段、剩余占位符都会明确报错 |
| `extract_pdf_text` | 只读 / 写 outputs | 文本型 PDF 按页提取（literal / hex / ToUnicode CMap 含中文）；扫描件明确提示不支持 |
| `plan_file_organization` | 读 + 写计划 | 计划校验（仅 move、inputs 内、无覆盖、无重名） |
| `apply_file_plan` | **审批后移动** | 预检 → 逐项移动 → 校验 → 变更清单；部分失败 → NEEDS_REVIEW |

## 数据发送范围（务必阅读）

任务运行时，以下内容会发送给所配置的模型服务：用户输入的任务描述、`inputs/` 下被读取文件的
**文件名、表头、预览记录与汇总计算结果、PDF 提取的文本**。文件读取仅限授权工作目录的 `inputs/`，
输出仅写入 `outputs/<任务ID>/`。文件整理在执行前需要你在对话框中明确同意。

## 构建与测试（开发者）

构建机需要 **.NET SDK 8.0+**（仅构建工具，产物仍是 net48，机器上无需安装 VS）：

```bash
dotnet build Win7WorkAssistant.sln
dotnet publish src/WorkAssistant.Desktop -c Release -o release

# 自动化验证（61 个用例：CSV/XLSX/DOCX/PDF/整理/会话/审批/SSE/Profile/回执/脱敏）
tests/WorkAssistant.Tests.Console/bin/Release/net48/WorkAssistant.Tests.Console.exe
# 重新生成演示样例（xlsx/pdf 由程序自身写出器构造）
tests/WorkAssistant.Tests.Console/bin/Release/net48/WorkAssistant.Tests.Console.exe --write-samples samples/workspace/inputs
```

## 工程结构

```
src/
  WorkAssistant.Core/            # DTO、接口、Agent 循环、任务类型/完成证据、提示词组装、Fake 模型
  WorkAssistant.Infrastructure/  # HTTP 模型客户端（非流式 + SSE 流式）、多 Profile 配置与 DPAPI 凭据、任务与会话存储
  WorkAssistant.Tools/           # 工作区策略、CSV/XLSX/DOCX/PDF 引擎（纯托管 OOXML/PDF 解析）、12 个内置工具
  WorkAssistant.Desktop/         # WPF MVVM：连续对话、队列、流式渲染、审批对话框、设置（多 Profile）
    Themes/                      #   Tokens.xaml / Controls.xaml / Icons.xaml（统一主题与矢量图标）
    Presentation/                #   ConversationItem / TaskStepVm / ArtifactCardVm（类型化展示模型）
tests/
  WorkAssistant.Tests.Console/   # 控制台测试跑批（61 用例）+ 样例生成器
samples/workspace/inputs/        # 标准演示数据（CSV ×2、sales.xlsx、产品说明.pdf）
release/                         # 发布目录（旧版备份于 artifacts/release-before-v1/）
docs/                            # 设计文档、实施进度、截图（docs/previews/v1-*.png）
```

依赖（版本锁定）：Newtonsoft.Json 13.0.3、CsvHelper 33.0.1、Microsoft.NETFramework.ReferenceAssemblies 1.0.3（仅构建期）。
**XLSX/DOCX/PDF 引擎为本仓库纯托管实现**（System.IO.Compression + 手写最小 OOXML/PDF 解析），无第三方文档库、
无 native 依赖——对应正式版 §11“文档引擎无 Win8/10 专属 API、native 清单为空”的 Win7 约束。

### UI 自动化验证

```bash
release/WorkAssistant.Desktop.exe --selftest-ui --result ui-selftest.txt   # 42 项断言（含多轮会话/问答/整理/回看）
release/WorkAssistant.Desktop.exe --demo-ui                                # 自动授权样例并跑一次演示
release/WorkAssistant.Desktop.exe --render-ui --scene home --output out.png
```

## 安全边界摘要

- 路径：拒绝绝对路径 / UNC / 设备路径 / ADS 冒号 / 保留设备名 / `..` 穿越 / 前缀绕过 / 符号链接与目录联接（逐级 ReparsePoint）；整理目标仅限 inputs/ 内且不得覆盖。
- 输入：单文件 10 MiB、每任务 50 MiB、10 万行、单字段 64 KiB；金额 strict-decimal（最多两位小数、允许负数）。
- 预算：普通任务默认128次模型编排请求、256次工具调用、30分钟；共享主/子任务编排上限256次。用户角色配置可收紧预算，参数错误受控反馈后仍无进展才终止。
- 输出：UTF-8 BOM + CRLF、金额固定两位、CSV 公式注入防护、SHA-256 成果清单、临时文件校验后原子提交、失败清理、不覆盖不同内容；XLSX 确定性生成（同输入同字节）。
- 有效性：模型“声称成功”不算成功——文件类任务必须有通过校验的成果（NO_ARTIFACTS）；问答轮以完整文本回复为证据，不虚构文件操作。
- 审批：apply 类敏感工具执行前必须用户逐项确认；未配置审批通道按拒绝处理（fail-closed）。
- 流式：工具参数片段收齐结束信号后才解析执行（不执行半截 JSON）；截断或显示文字后的断流可保留内容并受控继续；不把不完整工具参数判为成功。

## 已知限制与未验证项

- **Win7 未实机验证**：本仓库仅在本机（Windows 11）构建并验证（61 项核心测试 + 42 项 UI 自检 +
  三场景实际渲染，见 `docs/previews/v1-*.png`）；尚未在 Win7 SP1 x64/x86 目标机上验证字体、图标、
  对比度、缩放、TLS 与性能。不得据此声称兼容性验收通过。
- XLSX 读取支持第一个工作表的共享字符串/内联字符串/数值/公式缓存值；**日期以 Excel 序列值呈现**（不换算日历格式）；
  多字体 PDF 的 ToUnicode 映射按全文档合并（极复杂文档可能映射到非首选字体）；OCR/扫描件 PDF 明确报不支持。
- DOCX 模板的 `{{字段}}` 需完整位于单个文本块内（拆分到多个 run 会明确报错而非静默）。
- 文件整理只支持移动/改名（1.0 不删除文件）；部分失败后需人工复核，不自动重试。
- 任务与会话持久化使用SQLite及本地记录；上下文投影可压缩，完整工具结果与草稿仍在本地回查。
- 100%/125%/150% DPI 仅在本机 100% 下验证；系统高对比度模式未做专门适配。
- 真实DeepSeek接口已测试合成文档任务；具体数量、内容规模和未覆盖项以1.0.4验证记录为准，不能外推所有供应商或任意规模。
- 安装包/签名/自动更新（W08）与 x86 构建、企业网关部署（W01 PoC 项）不在本增量内。
