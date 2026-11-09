# 正式版工作包修复交付记录

日期：2026-11-01。依据：`PRODUCTION_IMPLEMENTATION_PLAN.md` 的 W01～W09，以及 `PRODUCTION_GAP_REVIEW_2026-11-01.md` 的 F01～F14。

本轮按要求修改实现代码，**不编写或运行测试工作**。文中的“已实现”只表示源码已落地、通过构建，不表示完成真机、协议、业务结果或发布验收。旧 `release/` 未覆盖。

## 1. 交付入口

- 源码：`src/`，新增 `WorkAssistant.Host` 项目，已加入解决方案。
- 编译候选：`artifacts/production-candidate/WorkAssistant.Desktop.exe`。必须连同 Host、DLL、配置以及 x86/x64 子目录一起使用。
- 本轮前源码副本：`artifacts/source-before-production-fixes-20261101/`。
- 安装器源码：`installer/WorkAssistant.iss`。
- 签名发布脚本：`scripts/Build-SignedRelease.ps1`。
- 第三方材料：`docs/THIRD_PARTY_NOTICES.md`、`docs/third-party-licenses/`。

## 2. 按工作包交付

| 工作包 | 本轮实现 | 当前边界 |
|---|---|---|
| W01 兼容性与模型 PoC | 环境诊断：系统、位数、.NET Release、组件、状态库/IPC 版本；安装器要求 Win7 SP1 和 .NET 4.8；TLS 1.2、默认系统证书验证 | Win7 x86/x64、本机 SQLite 加载、证书/代理/IP 网关的真实验证未执行，属于排除的验证工作 |
| W02 契约与任务类型 | 持久化 Step、调用参数和结果；工具版本、权限、风险、超时及输出契约；问答、分析、文件生成、文件修改采用不同证据；仅生成计划与实际修改分开 | 类型识别仍包含规则推断；不能把构建成功等同于业务验收 |
| W03 宿主/IPC | WPF → Host → 独立文档 Worker；受当前用户 ACL 保护的 Named Pipe、PID/nonce/协议握手、2 MiB 帧上限；Job Object 约束进程树、合计 768 MiB 提交内存；单工具 60 秒、任务 5 分钟、桌面监督 6 分钟 | Host 与 Worker 使用同一 EXE 的不同启动模式，实际是不同进程；不作为恶意代码安全沙箱 |
| W04 存储与迁移 | SQLite/WAL/FULL 同步；事务回执；JSON 历史导入、原件保留及迁移后备份；输入快照、持久聚合和计划；提交意图确认、提交清单、启动恢复核对；备份/恢复、保留期、配额与任务删除 | 新增状态保持 schema v1，兼容性升级采用拒绝更高版本写入；外部备份及工作区原件/成果不随任务清理自动删除 |
| W05 模型与对话 | 修复 Profile 串写与取消删除；配置指纹、能力与检测时间持久化；真实 probe 调用及参数核对；流式探测和已确认不支持时退回非流式；HTTP 头到达后立即读 SSE；响应/参数预算；完整消息与工具回执上下文；模型外发授权 | 不填写供应商价格时不虚构费用；真实服务探测功能已实现，但本轮未调用真实模型执行验证 |
| W06 办公工具 | XLSX 工作簿关系顺序、日期日历、公式缓存检查；CSV/XLSX 导出回读比对列名/分组/金额；Word 跨文本节点模板填充；PdfPig 页树/字体解析；Office ZIP/XML 资源边界与被动模板检查；文件整理句柄校验、摘要绑定与持久变更记录 | 不含 OCR、Office COM、PPTX；复杂排版保真不作承诺；源文件变化或提交状态不确定时拒绝重放 |
| W07 GUI 正式流程 | 附件副本导入、模型数据发送授权、业务口径询问、审批；持久队列和取消；任务中心、步骤与变更记录；新任务方式重试；独立进程安全预览；备份/恢复/会话导出/清理；退出时可转入托盘继续执行 | 崩溃或取消后队列暂停；重启后排队任务供任务中心逐项处理，不静默自动执行；NeedsReview 不提供一键重放 |
| W08 安装更新 | 当前用户安装、分版本目录、框架检查、签名安装器/卸载器脚本；CMS 签名清单、发布证书固定、HTTPS、摘要/大小/渠道/schema 校验；安装前备份；兼容旧版本切换；正式发布打包脚本与许可证材料 | **未提供发布证书，且当前未安装 Inno Setup 编译器/签名工具，因此未生成正式签名安装包。开发候选的更新安装默认禁用。** |
| W09 系统测试与试点 | 按要求未实施 | 未运行单测、UI、故障注入、压力、Win7 真机、真实模型和试点验收；原有测试不作为本轮修复证明 |

W01 的真机/服务验证和 W09 不在本轮范围。W08 的签名制品需要实际发布证书和构建工具，属于尚缺的外部发布输入，不能用自签证书或未签名安装包冒充完成。

## 3. 审查问题逐条对应

以下均为“代码已修正，未执行回归验证”。

| 编号 | 修正内容 | 主要源码 |
|---|---|---|
| F01 | 用已显示 Profile 保存旧表单；删除凭据推迟到保存；配置能力绑定实际检测指纹 | `Desktop/SettingsWindow.xaml.cs`、`Infrastructure/SettingsStore.cs` |
| F02 | 复用现存输出前比较完整字节；不覆盖冲突；持久提交清单、Flush、解析和回读验证 | `Tools/TableReading.cs`、`Tools/ExportValidators.cs` |
| F03 | 计划成果不能代替实际修改证据；显式“仅计划”单独处理并禁止变更工具 | `Core/AgentController.cs`、`Core/SystemPrompts.cs` |
| F04 | 分析必须有计算工具证据；统计文件也要求计算证据；非正常 finish_reason 不作为完整响应；未完成产出标告警 | `Core/AgentController.cs`、`Infrastructure/HttpModelClient.cs` |
| F05 | 可选 PDF 导出以实际 artifacts 判定；只读提取不强制登记文件；提取范围显式返回 | `Tools/ExportXlsxTool.cs`、`Core/AgentController.cs` |
| F06 | 输入摘要绑定批准计划；句柄锁定与改名；先落盘变更意图；中断或部分移动进入 NeedsReview | `Tools/VerifiedFiles.cs`、`Tools/FilePlanTools.cs`、`Host/IsolatedTool.cs` |
| F07 | 普通演示模式不再自动批准真实工作区文件修改，仍走桌面确认 | `Desktop/MainViewModel.cs` |
| F08 | ZIP 展开/压缩比/部件数量、XML DTD 与字符上限、列/单元格上限；文档独立进程、内存与硬超时 | `Tools/DocumentLimits.cs`、`Tools/PackageZip.cs`、`Host/IsolatedTool.cs` |
| F09 | ResponseHeadersRead，收到响应头后直接处理有界 SSE 流，保留整体读取时限 | `Infrastructure/HttpModelClient.cs` |
| F10 | 必须实际调用 ping_probe 并匹配 nonce；区分认证未验证；配置指纹不匹配失效；不把上次检测称为当前在线 | `Infrastructure/HttpModelClient.cs`、`Desktop/SettingsWindow.xaml.cs` |
| F11 | 按 workbook.xml 的 sheets 关系解析首工作表；移除 sheet1/任意 entry 的兜底猜测 | `Tools/XlsxReading.cs` |
| F12 | 采用 PDF 页树和字体解析；保留页码；按图像信息区分疑似扫描页；标出实际提取页范围 | `Tools/PdfText.cs` |
| F13 | 保存本轮完整消息/工具调用/结果；输入快照 manifest、稳定 file_id、来源摘要和工具版本；不再截断为 600 字上下文 | `Core/TaskRecords.cs`、`Host/FileReferenceTool.cs`、`Desktop/MainViewModel.cs` |
| F14 | SQLite 事务、耐久替换、准备/提交状态、回执与提交清单；不确定时人工复核，禁止盲目自动重放 | `Infrastructure/SqliteStateStore.cs`、`Tools/OutputRecovery.cs` |

表内项目路径均相对于 `src/WorkAssistant.` 对应项目，例如 `Desktop/MainViewModel.cs` 指 `src/WorkAssistant.Desktop/MainViewModel.cs`。

## 4. 运行及恢复契约

### 4.1 进程与 IPC

桌面只负责交互和决策；Host 管理模型及任务；每次文档工具调用启动一个 Worker。Worker 不接收模型凭据。没有默认网络监听端口。

协议 v1 使用 4 字节长度前缀和 UTF-8 JSON，帧包含 `version/type/payload`，最大 2 MiB。随机管道名配合当前用户 ACL、对端 PID 与一次性 nonce。一个 Host 对应一个任务，一个 Worker 对应一个工具调用；任务 ID、步骤 call ID 关联持久状态。

`checkpoint → checkpoint-ack` 要求宿主确认 Prepared/Committed 状态已写入数据库，Worker 才继续提交。终止 Worker 不代表修改一定没有发生，修改工具必须转入复核。

### 4.2 文件引用与来源

输入快照保存在 `tasks/<task_id>/snapshot/inputs/`，manifest 含 file_id、相对路径、字节数与 SHA256。file_id 由相对名称和内容摘要派生，同文件版本可以跨轮识别。

在线模型的 `path/paths/template_path/operations.source` 参数使用 file_id。Host 将其解析为快照路径，核对摘要并保持只读句柄至工具返回。新整理目录和新文件名仍是受限的 `inputs/` 相对目标，不允许物理路径。

成果带内容摘要、来源快照摘要、工具名及版本。每轮任务单独输出目录，重试创建新任务；不覆盖旧任务成果。

默认界限：单文件 10 MiB、输入快照合计 100 MiB/1000 文件、Office 展开合计 64 MiB/2000 部件、ZIP 高压缩比拒绝、XML 禁 DTD、最多 16384 列、20 万共享字符串/单元格字符串 65536 字符；工具参数 64 KiB、工具结果上下文 64 KiB、总对话 512 KiB 字符预算、模型响应 2 MiB。超限明确报错，不静默截断参与计算的数据。XLSX 要求首行唯一且非空的列名，不自动猜测多层表头；Word 字段填充以正文段落和表格为范围。

### 4.3 崩溃与取消

- 任务启动先检查工作区独占文件锁。
- 正在等待批准的 Pending 步骤没有取得变更执行资格。
- 变更步骤进入 Started/Prepared/Committed 后异常退出，标记 NeedsReview；不按旧计划自动再次移动。
- 文件成果提交清单支持检查“文件已提交但回执尚未落库”；保留可验证成果，不补做不确定副作用。
- 中断、取消、待复核后暂停后续队列，防止继续写入状态不明的工作区。
- 从任务中心重试时生成新任务和新输出目录；待复核文件必须人工对照变更记录后创建新的明确请求。

### 4.4 本地数据与诊断

SQLite 保存本地对话、工具结果和任务状态，属于用户数据。事件日志不重复保存完整流式正文；脱敏诊断只包含环境、组件、状态数量和错误码，不导出密钥、模型地址、任务正文、文件内容或输入路径。

任务清理处理本地任务记录和快照，保留工作区输入、成果和已有备份。保留策略需在维护窗口保存并显式执行清理。状态库备份不代替工作区/快照文件备份；恢复状态库不能撤销已经发生的文件移动。

## 5. 构建与正式发布

开发候选构建（不运行测试）：

```powershell
dotnet restore src/WorkAssistant.Desktop/WorkAssistant.Desktop.csproj -m:1 --locked-mode
dotnet build src/WorkAssistant.Desktop/WorkAssistant.Desktop.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false -o artifacts/production-candidate
```

本轮最终构建输出及候选文件摘要见候选目录的 `BUILD_INFO.json`、`BUILD.log`。目标是 .NET Framework 4.8；构建机是当前开发机，不是 Win7 真机验收。

正式发布需在现代构建机具备：.NET SDK、Inno Setup 6、Windows SDK SignTool、当前用户证书存储中的有效代码签名证书及私钥。先完成独立的验收门槛，再运行发布脚本：

```powershell
./scripts/Build-SignedRelease.ps1 `
  -Version 1.0.1.0 `
  -CertificateThumbprint '<40 位证书指纹>' `
  -InstallerUrl 'https://updates.example.com/stable/WorkAssistant-1.0.1.0-setup.exe' `
  -SignTool 'C:/path/to/signtool.exe' `
  -InnoCompiler 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe' `
  -Channel stable
```

脚本不会运行测试，不会自动发布到服务器。输出位于 `artifacts/signed/<version>/`。发布者证书 SHA256 在编译时嵌入；开发构建为空时，更新安装失败关闭。

安装在当前用户的 `Programs/Win7WorkAssistant/versions/<version>/`，升级保留旧目录。更新清单为封装内容的 CMS `.p7m`，检查发布者、证书链、渠道、版本、有效期、schema、安装包 HTTPS 地址、大小及摘要，再检查安装包 Authenticode。

旧版本切换还核验该版本 `release.p7m` 和 EXE 摘要，且必须兼容当前状态库。schema 不兼容时禁止直接回退，需恢复对应备份和对应应用版本；不会假装可以自动逆转任意数据库迁移。证书续期/更换发布者需另行维护受信发布流程。

## 6. 本轮未执行的事项

1. 未运行原有或新增测试套件，未启动 GUI 验证，没有执行真实模型任务。
2. 未验证 Win7 真机、Excel/WPS、实际 PDF 样本、DPI/键盘/托盘行为。
3. 未做故障注入、SQLite 损坏/断电、进程强杀、资源极限、安装中断与回滚实验。
4. 未生成正式签名安装包，未签名候选 EXE/DLL，未安装或发布到用户原 `release/`。

后续工具应以本记录和当前源码为实现基线，以原审查报告为待回归问题清单；不得直接沿用旧测试通过数量来关闭这些问题。

## 7. 后续卡顿修正与技能现状

> 本节保留上一轮记录。随后已实现技能工作室与应用内 skill-creator，当前技能状态以 `SKILLS_AND_GUI_V4_2026-11-01.md` 为准，下文“没有技能系统”不再描述最新代码。

- 历史列表读取和状态汇总移出 WPF Dispatcher；后台生成完整快照后再更新界面，过期刷新不覆盖新结果。
- 历史状态查询只解析任务开头的 state，不反序列化完整消息、工具结果及成果。
- AssistantDelta 不再触发整个 Task 的检查点写入；模型响应、步骤、审批和文件提交的关键持久化检查点保留。
- 只进行了构建，没有性能测量、GUI 或回归测试，不能据此承诺全部卡顿消除。每次工具创建独立 Worker 的冷启动开销仍存在。

当前运行时内置 14 个工具、5 个快捷任务，没有独立的 skill 注册、参数表单、版本和导入管理系统。开发环境安装的 Codex ui-ux-pro-max 不属于此软件的运行时技能。

可优先基于现有工具封装的内置技能：销售 CSV 汇总、Excel 筛选映射与汇总、Word 模板/报告生成、PDF 带页码提取、文件归档计划与确认执行。这些是后续技能产品化范围，不能将现有快捷提示词标成已实现的完整技能系统。
