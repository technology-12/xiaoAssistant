# 本地并发子 Agent 实现与交接

> 后续内核改造已支持同批依赖、成果接力与工作区权限，最新行为以 [OpenCode 本地协作适配](OPENCODE_NATIVE_COLLABORATION_2026-11-03.md) 为准；下文保留初版交接记录。

日期：2026-11-03。候选程序：`artifacts/subagent-candidate/WorkAssistant.Desktop.exe`。保留原 native-candidate；运行新版前退出旧程序和托盘实例。

## 使用

在“模型与偏好设置”的高级设置启用“本地并发子 Agent”（默认启用）。子 Agent 模型默认跟随主模型，也可选择另一个已配置的模型 Profile；不同厂商分别配置 API URL、模型名和凭据。当前通过 OpenAI-compatible HTTP 接口访问模型，需要服务支持工具调用。未适配其他厂商专有协议。

请求示例：“把这两份文件分别交给两个子 Agent 阅读，各自列出风险，再由你综合比较。”主模型通过 delegate_tasks 自主拆分；开关开启不会让所有任务强制并行。演示模式不能验证真实模型的拆分能力。

## 本地边界和流程

Desktop → 本地 Host → 主 AgentController → delegate_tasks → 两个独立子 AgentController → 本地隔离 Worker → 本地 SQLite。只有模型推理经 HTTP 请求指定服务，无远程调度器、远程文件执行或远程任务数据库。模型请求包含明确分配的提示、上下文和必要文件内容，仍需按已有界面确认发送。

每个主任务最多创建 4 个子任务，最多同时运行 2 个，依赖关系通过多批派发实现，不包含任意 DAG 调度。子任务有独立消息历史、任务 ID、存储目录和工具结果；不会占用聊天侧栏的普通轮次。主任务收到摘要，可通过 read_task_history 回查同一会话的完整子任务记录。

子 Agent 继承主任务的读写模式。除 8 个读取/计算工具外，可使用 export_summary、export_xlsx、generate_docx、fill_docx_template 直接写出 CSV/Markdown/Excel/Word。仅开放主任务当前技能及模式已允许的工具；主任务只读时，子任务不能生成文件。文件引用必须由主任务明确分配，省略 file_ids 表示不分配文件。不可遍历其他文件、读其他会话、修改原始输入、向用户提问或继续派发子 Agent。

子任务直接在 outputs/<child_task_id>/ 生成成果，同名文件分属不同目录，不互相覆盖。已验证成果及其哈希自动汇入主任务成果列表，主模型同时收到路径和来源，不必重复导出。聚合结果 ID 仍仅在所属任务内有效，由生成该聚合的子任务直接导出。工具审批规则沿用主流程，审批请求串行展示，避免并发响应串线。当前写能力为工具支持的文件生成，不包含任意文本编辑或并发改名、移动原始输入。

## 限额、故障和恢复

- 每个子任务：最多 8 次模型请求、12 次工具调用、180 秒；父任务仍有 600 秒总时限。
- 主任务及所有子任务共享 64 次模型请求预算，包含上下文摘要请求；不逐次统计 HTTP 客户端内部传输重试。
- 本地 Job Object 限制最多 3 个进程（Host 和两个工具 Worker）及合计 1 GiB 内存；无 Node/Bun 依赖。
- 用户取消传播到运行中及排队子任务。单个子任务失败不取消兄弟任务；主任务标记 CompletedWithWarnings / SUBAGENT_FAILED，并把警告写入最终答复和后续上下文。
- 子任务的模型别名、模型名、状态、事件、用量和父子关系落盘。异常退出留下的运行中/排队子任务标记中断，不自动重放。
- 子任务模型凭据沿用 Profile 的 DPAPI 保存机制；偏好设置只记录 Profile 名称。主任务用量合并子任务返回用量，不虚构认证失败时服务未返回的 token 数。

## 代码入口

| 文件 | 职责 |
|---|---|
| src/WorkAssistant.Core/SubAgents.cs | 并发队列、配额、取消与共享模型预算 |
| src/WorkAssistant.Host/DelegateTasksTool.cs | 派发协议、文件授权、独立控制器、结果汇合 |
| src/WorkAssistant.Host/Program.cs | 本地主循环工具注册及共享预算 |
| src/WorkAssistant.Core/AgentController.cs | 子任务资源上限及部分失败语义 |
| src/WorkAssistant.Infrastructure/LocalTaskStore.cs | 父子任务持久化 |
| src/WorkAssistant.Infrastructure/LocalConversationArchive.cs | 同会话子任务回查 |
| src/WorkAssistant.Infrastructure/SettingsStore.cs | 开关和子模型偏好 |

## 验证与限制

Release 构建成功，0 警告、0 错误。原内核 36 项回归、新增调度与持久化 16 项检查全部通过。真实 C# HTTP 客户端对接本机模拟模型 API，成功场景 7 项、认证失败场景 5 项检查通过；观测到子请求并发峰值为 2，两个 API 路由及各自凭据、文件隔离和本地 Worker 均经过验证。

检查脚本：artifacts/diagnostics/subagents/verify.ps1、http_integration.py，以及 artifacts/diagnostics/native-runtime/verify.ps1（支持 CandidatePath 参数）。全部使用合成数据和凭据。

新增 6 项真实 HTTP + 本地 Worker 写入检查通过：两个子任务实际导出同名 Excel、输出目录隔离、主任务成果合并、哈希登记及原始输入保持不变。以前测试输出中的“children cannot generate user file artifacts”只表示该阅读场景没有生成成果，不代表当前权限规则。

尚未验证 Win7 实机、GUI 手工操作及真实厂商 API 的稳定性与自主任务拆分质量。本轮交付为功能候选包，非签名安装发行版。未实现子任务人工接管、任意 DAG、任意文件编辑、崩溃自动续跑或跨任务聚合结果导入。
