# WorkBuddy 国产 Linux 迁移：AI 开发执行计划

日期：2026-11-09。版本：计划 v1.1。状态：**计划已编制，迁移开发尚未开始**。

适用基线：晓助手 1.0.7；来源 Git commit SHA `4ef75b18004ec7e902d46cb9d4d69270e5ad2655`，tree SHA `c7cea0e5fe084101c20b18b9a470763c0e44beba`（已由真实 Git 检出核对）。

配套文件：[开发环境评估](DEVELOPMENT_ENVIRONMENTS.md)、[迁移技术方案](MIGRATION_PLAN.md)、[源码下载核查](research/source-snapshot-2026-11-09.json)、[机器可读任务表](tasks.json)。

## 1. 项目目标和边界

保留本机 C# Agent 内核、模型协议、审批与成果验证体系，建立 .NET 10 + Avalonia 的 Linux 版本。默认首发 UOS V25 amd64，随后适配 UOS V20、UOS arm64、银河麒麟 V10/V11，再扩展 deepin/openKylin。LoongArch 先给出可行性结论，不预先承诺交付。

Mac 或 Windows 均可承担全部 Linux 代码开发；当前设备可继续作为主开发机；UOS/Linux 负责目标实现与发布验证。统信版的任何功能都不要求先在 Windows 实现。仅当同时承诺维护旧 Windows 产品时，另行加入 Windows x64 回归；它不是统信迁移任务的依赖。当前 Mac 的 SDK 和 Avalonia 冒烟测试尚未通过，先做准备任务。

不把迁移顺带扩大为重新引入 OpenCode 网关、改写全部业务内核、重做 XP 产品、发布完整 Mac 产品或未经需要升级所有依赖。原文件编辑无法保持所需语义时，明确禁用该能力，保留生成新文件成果；不得静默降级。

## 2. AI 执行约定

1. 每轮先读取本计划、任务清单、实际代码和上一轮交接；只领取依赖已经完成的任务。不要默认一次执行整个迁移。
2. 每个任务以单一可验证结果为单位，通常半天到两天；复杂文件安全或 IPC 任务再拆分为设计、实现、故障验证。文档中的人日不是 AI 的必达时限。
3. 先写清平台契约和验收条件，再写平台实现。测试覆盖越界、失败、竞争、取消和恢复等行为，不照抄实现生成断言。
4. 不删除安全校验，不用空实现、硬编码成功、`catch { return true; }` 或假回执通过回归。假模型仅用于明确的演示/测试路径，不进入正式执行路径。
5. 不能把“编译通过”“交叉发布成功”“Mac 上运行”“模拟测试通过”写成 UOS/麒麟验收通过。缺环境则标为未验证，保留可执行的待验收条目。
6. 保留旧 Windows/XP 源码基线，Linux 使用独立入口；不要求维持所有旧目标可运行才能推进 Linux。仅在修改被旧产品实际采用的共享代码且同时承诺维护旧产品时，安排 Windows 回归；其结果仅约束 Windows 交付，不阻挡隔离的统信分支。避免先全局替换 `net48`、路径分隔符或大小写比较。
7. 密钥通过应用配置或测试注入，禁止写入源码、提示词、证据包和日志。使用合成文档；真实模型调用单列范围与费用，不混入默认回归。
8. 每轮完成相关检查后提交交接。未经用户授权不推送、合并或发布；本地可验证的实现与文档不逐步索取重复确认。
9. 本计划不自动启用多代理。默认一个执行者按依赖推进；需要委派时另行明确。接力开发使用任务编号和交接文件，不依赖聊天记忆。
10. 不因调研快照曾缺少 29 个测试文件而编造“原始测试重建成功”。恢复原件或明确建立新的行为测试，两者分开记录。

## 3. 启动前的环境和源码门禁

| 环境 | 必做工作 | 验收证据 |
|---|---|---|
| 选定开发机（当前 Mac arm64，也可 Windows/Linux） | 确认 SDK；最小控制台和 Avalonia 启动；中文、文件选择、SQLite 加载 | OS/架构、SDK、版本锁、命令与退出码、截图或日志 |
| Windows x64（可选） | 仅同时维护 Windows 产品时：准备依赖、运行既有验证 | 被测二进制散列、实际通过/失败/跳过数 |
| UOS amd64 | 镜像、内核、glibc、桌面会话、用户服务/控制器与原生库探测 | 环境 JSON、最小应用和 SQLite/OCR 探针结果 |
| Git 基线 | 补齐缺失测试，确定提交 SHA；核对内容和目录树 | 来源、提交/树 SHA、文件核查、工作区状态 |

2026-11-09 调研使用的本地快照缺 29 个测试文件且不含 Git 历史；这是调研环境记录，不表示远端缺文件。执行时先检查实际 checkout 的 `git status`、提交和测试完整性；完整检出无需重建 Git 或重复补齐文件。若当前仍为不完整快照，再恢复原件并保留版本差异，不覆盖用户修改。

准备阶段可执行的只读命令：

```sh
# macOS：当前命令行可执行；dotnet 如仍缺失则记录，不伪造输出。
sw_vers
uname -m
command -v dotnet
# 只有找到 SDK 后再执行：
dotnet --info
```

```sh
# 在实际 Linux/UOS 环境执行，不在 Mac 上代替执行。
cat /etc/os-release
uname -m
uname -r
getconf GNU_LIBC_VERSION
systemd --version
```

本计划不附无人验证的“一键安装全部依赖”脚本。SDK/Avalonia/原生库先锁版本和来源，再按环境部署。

## 4. 目标代码结构与构建策略

下面是**拟新增结构**，目前这些入口尚不存在：

```text
Xiao.Linux.sln
global.json
src/WorkAssistant.Platform.Abstractions/
src/WorkAssistant.Platform.Linux/
src/WorkAssistant.Desktop.Avalonia/
tests/WorkAssistant.CrossPlatform.Tests/
tests/WorkAssistant.Linux.IntegrationTests/
scripts/linux/verify.py
scripts/linux/package.py
docs/linux-port/ENVIRONMENTS.md
docs/linux-port/CONTRACTS.md
docs/linux-port/PROGRESS.md
docs/linux-port/handoffs/<任务ID>.md
```

优先保留 Core、Tools、Infrastructure 的既有业务代码。平台边界提取后，使用有条件的多目标或现代入口共享源码；具体方式由 T03 的构建验证决定。`Directory.Build.props` 中 net48、引用程序集、binding redirect 等属性应限制到相应目标，不能污染 net10 图。

拟抽象：`IAppPaths`、`ICredentialStore`、`IHostTransport`、`IProcessSupervisor`、`IVerifiedFileSystem`、`ITextCommitter`、`IDesktopIntegration`、`IUpdateService`。T02 需确定每个接口的输入、失败类型、取消、能力探测和批准绑定语义。

首个 Linux 垂直闭环在全 UI 完成前运行：FakeModel → 授权样例工作区 → 独立 Host → 安全读取 → 生成新成果 → 核验并持久化 → 重启回查。关键依赖准备好后，在 UOS 尽早验证，不到收尾才第一次启动。

## 5. 可领取任务与依赖

全部任务初始为 TODO；文档编制不代表下表任务已完成。机器可读任务表记录依赖、目标文件、环境、验收、证据与当前状态。

| ID | 任务 | 前置 | 完成标准 |
|---|---|---|---|
| T00 | 完整源码与迁移基线 | 无 | 核对实际 checkout 完整性，缺失时恢复原件；冻结真实 Git 提交；无需 Windows 才能完成 |
| T01 | 开发环境与目标系统探针 | 无 | 选定开发机 SDK/最小程序验证；可访问 UOS amd64；失败有替代开发环境 |
| T02 | 平台契约和能力模型 | T00 | 逐项定义文件、事务、IPC、进程、凭据契约；给出旧版行为映射与不可等价项 |
| T03 | 现代构建与回归入口 | T00、T01、T02 | 新 net10 解方案可构建；旧源码基线保留且构建入口隔离；锁定 SDK/依赖并接入证据格式 |
| T04 | 共享 Agent 与模型协议迁移 | T03 | FakeModel、多轮、预算、审批拒绝、取消、子任务和协议解析通过相关回归 |
| T05 | XDG、凭据与 SQLite | T03 | Linux 目录/Secret Service；旧数据备份导入；WAL、并发、恢复和凭据不可用路径验证 |
| T06 | 安全打开与配置文件读取 | T02、T03 | workspace 与项目配置均使用安全文件层；链接、越界、大小写、目录替换测试通过 |
| T07 | 成果提交、移动与恢复 | T06 | 同卷提交、禁止覆盖、审批基线、异常/中断恢复验证；不夸大对外部写入者的隔离保证 |
| T08 | Host 与 Worker 通信 | T04、T05 | 两条 IPC 通道均有权限、对端、nonce、帧限制、取消和批准绑定验证 |
| T09 | 进程树与资源限制 | T08 | UI/Host 崩溃、超时、OOM 无持续写入 Worker；探测 cgroup 能力；线程预算合理 |
| T10 | 原文件编辑能力门禁 | T02、T07 | 首版默认禁用不能证明的覆盖操作；提案/审批/新文件交付可用；界面及工具描述一致 |
| T11 | UOS 无 GUI 垂直闭环 | T04、T05、T07、T09、T10 | 在真实 UOS amd64 跑通独立 Host、读取、生成、拒绝、取消、持久化和重启回查 |
| T12 | Avalonia 基础界面与 MVVM | T03、T04 | 项目、会话、输入、流式消息与基础设置可用；视图模型不直接依赖 WPF |
| T13 | 桌面集成、审批与任务体验 | T11、T12 | UI 接真实 Host；审批/队列/成果打开/中文输入/缩放验证，UOS 上可走完整流程 |
| T14 | 办公文档和文本 PDF 对等回归 | T04、T07 | CSV/XLSX/DOCX/PPTX/PDF 合成样例，数值/结构校验和目标办公软件呈现验证 |
| T15 | Linux OCR 依赖和受控执行 | T09、T14 | 原生库按 RID 加载；中英扫描/混合页、缓存、超限、取消、低置信度验证 |
| T16 | deb、自包含包与可信升级 | T05、T13、T14、T15 | 干净 UOS 安装/升级/卸载；数据保留、架构匹配、签名/摘要；不复用 EXE 更新器 |
| T17 | UOS V25 amd64 首版验收 | T16 | 支持范围内功能、安全、文档和系统门禁通过；未验证项公开列出；保存发布证据 |
| T18 | UOS V20 与 arm64 扩展 | T17 | 每个 OS×架构单独验收；旧系统库、GPU 和 OCR 不借用 V25 amd64 结论 |
| T19 | 麒麟、deepin、openKylin 扩展 | T18 | 每个目标组合保存安装、业务、桌面与安全能力结果；可选功能差异有说明 |
| T20 | LoongArch 全链路预研 | T02、T03 | 运行时、Skia、SQLite、OCR、工具链形成证据；输出继续/替代路线/暂缓结论 |

T12、T14 等依赖就绪后可按反馈优先级调整领取顺序，不意味着自动启动多个代理。T17 不等待 T20；没有龙芯依赖链证据也不影响已明确范围的 amd64 交付。

T00 不包含必须完成的 Windows 实机回归。没有 Windows 设备也可以完成 T00，并继续全部 Linux 迁移任务。若另行维护 Windows 产品，把对应回归记在条件性验证记录中；Windows 未验证只影响旧产品兼容/发布声明，不阻挡独立统信版本。

## 6. 关键任务的实现要求

### T06/T07/T10：文件和事务

阅读 `VerifiedFiles.cs`、`WorkspacePolicy.cs`、`AtomicTextTransaction.cs`、`DurableFiles.cs`、`ProjectConfigurationFiles.cs` 及调用者。安全校验必须到实际打开/操作的文件对象；不以 `Path.GetFullPath`、字符串前缀或一次 `realpath` 检查代替。

Linux 采用根目录 fd、受限解析和文件身份验证，探测内核能力；测试要包含校验与打开之间的替换、父目录重命名、硬链接及目标抢占。只读安全、移动、应用自管成果提交、用户原文件覆盖分别定义契约。

`flock`、摘要复核加 rename 不能直接视为 Windows TxF 等价实现。首版对不满足契约的原文件覆盖明确返回能力不可用，批准后也不能绕过能力检查。保留 Windows 实现，避免为了 Linux 统一为更弱的公共实现。

### T08/T09：Host 和 Worker

同时检查 `TaskHostClient.cs` 和 `Host/IsolatedTool.cs`。UDS 对端身份、nonce、任务和批准绑定、帧长度、断开后的状态复核全部有反向测试。进程清理必须覆盖 UI 退出、Host/Worker 异常、取消和资源超限。

systemd/cgroup 控制器可用性按运行环境验证；不能把 `TasksMax=3` 当作“允许三个进程”，因为线程也消耗任务预算。拒绝资源边界无法建立的执行模式，不默认为无限制运行。

### T05/T14/T15：状态、文档与原生依赖

SQLite provider 替换后验证备份、WAL、忙等待、连接池和 schema；旧 Windows 密钥默认重新输入，不导出明文。文本 PDF 先验证，再验证 OCR。OCR 的 traineddata 可复用，Windows DLL/VC++ 运行库不直接进入 Linux 包。

文档结构与重新解析校验通过后，还需在目标 WPS/LibreOffice 打开样例检查中文、表格与分页。不能把局部支持扩写为支持所有 Office/PDF 格式。

## 7. 统一验证入口与 CI

T03 建立统一入口后，才允许使用下面的**拟定命令**；当前不应直接执行或宣称成功：

```sh
dotnet restore Xiao.Linux.sln --locked-mode
dotnet build Xiao.Linux.sln -c Release --no-restore
dotnet test tests/WorkAssistant.CrossPlatform.Tests -c Release
python3 scripts/linux/verify.py --suite integration --evidence artifacts/linux-port/current
python3 scripts/linux/package.py --rid linux-x64 --format deb
```

实现脚本时明确参数、退出码、suite、清理规则和证据路径，并同步修订本计划。命令缺入口时返回失败，不通过空脚本占位。如维护 Windows 旧版，使用其原回归入口单独验证；Linux 行为测试使用现代入口，不要求运行旧 Windows 测试 EXE。

| 验证层 | 运行位置 | 频率/触发 |
|---|---|---|
| 共享内核/协议/文档行为 | 选定开发机（Mac 或 Windows）和 Linux/UOS | 相关实现变化时 |
| Linux 文件/IPC/资源/凭据集成 | Linux amd64，随后 arm64 | 对应平台实现变化时 |
| 旧版 net48 回归（条件性） | Windows x64 | 仅同时维护/发布 Windows 产品，且相关代码变化时 |
| UOS 界面与安装 | 锁定的 UOS 镜像与真实整机 | 每个候选发布，重大 UI/依赖变化提前验证 |
| 多发行版组合 | 各明确目标 | 每个声明支持的发布版本 |

CI 可以负责构建和无 GUI 验证；桌面、驱动、输入法和安全策略还需对应会话/整机证据。没有相应 runner 时记录为未验证，不能静默跳过后生成全绿报告。

## 8. 完成证据与交接格式

每个实现任务生成 `docs/linux-port/handoffs/<ID>.md`，内容包括：

```text
任务：Txx；状态：TODO / IN_PROGRESS / BLOCKED / DONE
源码：提交 SHA；若为快照，写 tree SHA 和缺口
环境：OS、小版本、CPU、内核、glibc、会话、SDK/依赖版本
结果：完成的用户行为与平台契约
变更：文件列表、Windows/其他平台影响
验证：实际命令、退出码、用例数、失败/跳过原因、日志路径
证据：构建/包散列、合成输入、关键输出校验、截图（适用时）
风险：尚未满足的契约；未运行环境；不能外推的范围
下一步：依赖已满足的下一任务编号与具体输入
```

自动化结果至少记录：`task_id`、`source_revision`、`os`、`architecture`、`runtime`、`suite`、`started_at`、`duration`、`passed`、`failed`、`skipped`、`exit_code`、`artifact_hashes`。平台状态区分 planned、build_passed、runtime_passed、acceptance_passed，不合并为一个布尔“支持”。

DONE 要求该任务承诺环境的验收满足并有证据；缺环境为 BLOCKED 或进行中，交接中写清已完成子项。补记缺口不等于可以伪造完成状态。

## 9. 排期与发布分层

| 时段（单人排期示意） | 目标 |
|---|---|
| 第 1 周 | T00–T03：完整基线、环境、契约与现代构建 |
| 第 2–3 周 | T04–T07：共享业务、状态、文件安全与成果路径 |
| 第 3–5 周 | T08–T11：隔离执行、能力门禁与首次 UOS 垂直闭环 |
| 第 5–8 周 | T12–T15：界面、办公文档与 OCR |
| 第 8–12 周 | T16–T17：安装、缺陷收敛、UOS amd64 首版 |
| 后续至第 12–18 周 | T18–T19：UOS 老版本/arm64 与多系统适配；T20 另出预研结论 |

周次有交叠，表示同一执行者根据依赖反馈穿插处理，不是阶段人日相加或多代理并发承诺。首版估算约 35–54 人日；完整矩阵额外约 15–30 人日；设备、原生库或契约问题发生时重新估算。

AI 可以加快代码和验证脚本编写，但不能免除目标机、原生库验证、安全语义评审和试点时间。不能未经实测承诺“AI 几天完成全平台”。

首版无原文件等价事务覆盖时，发行说明必须明确范围；若业务要求完整等价，则增加专项设计/验证任务并重排计划。OCR 无法纳入首版时，也必须修改支持范围与任务依赖，不能跳过 T15 仍标记原范围验收通过。

## 10. 可直接交给 AI 的启动指令

```text
请基于《WorkBuddy-AI开发执行计划.md》和机器可读任务清单开始准备阶段。
先处理 T00 的源码完整性与基线核查，以及 T01 的开发环境探测。
调研时是 macOS arm64、命令行未找到 dotnet，使用的快照缺 29 个测试文件且无 Git 历史。
请先检测当前实际环境与 checkout；完整 Git 仓库无需重建，已恢复文件无需重复补齐。
先读取现有文件和下载核查记录，确认当前状态；恢复文件时验证来源和 Git blob 散列。
对已有文件和用户修改做保护，不覆盖不同内容，不把 tree SHA 当成 commit SHA。
完成可独立的只读核查和准备，列出必须补齐的环境；需要安装时说明版本、来源和验证方法。
本轮不改 WPF/XP 业务代码，不删除安全约束，不运行真实模型任务，不发布软件。
输出 T00/T01 的真实完成子项、待办、环境记录和下一步，不声称迁移已经完成。
```

单任务接力指令：

```text
执行任务 <ID>。先核对依赖状态和前序交接，读取任务目标及相关源码。
先说明本轮要实现和验证的行为，再做范围内改动；发现契约冲突则更新设计并说明影响。
运行适合本次改动的检查，保存真实证据；当前平台不能验证的内容单列。
更新该任务状态与交接，报告改了什么、为什么、怎样验证、剩余风险和下一任务。
不要把缺失实现伪装成成功，不自动扩展到未领取任务或发布。
```

## 11. 官方依据

- [WPF 的 Windows 平台限制](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
- [.NET 在 macOS 的 SDK 安装与架构选择](https://learn.microsoft.com/en-us/dotnet/core/install/macos)
- [.NET 10 运行平台矩阵](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [Avalonia 平台矩阵](https://docs.avaloniaui.net/docs/supported-platforms)
- [Avalonia Linux、X11/Wayland 与 WSL 说明](https://docs.avaloniaui.net/docs/platform-specific-guides/linux)

以上是平台事实依据；任务拆分、环境分工、依赖和工期为结合本仓库作出的工程设计。
