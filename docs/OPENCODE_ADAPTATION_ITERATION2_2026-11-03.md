# OpenCode 本地内核适配：第二轮交接

日期：2026-11-03。候选程序：`artifacts/iteration2-candidate/WorkAssistant.Desktop.exe`。上一轮 `artifacts/subagent-candidate` 保留。本轮仍使用本地 C#/.NET Framework 4.8 内核；只有模型推理可以访问配置的 API，无远程文件执行器或 Node/Bun 依赖。

## 本轮三个增量

### 2A：模型重试和超时判定

参考固定版本 OpenCode 的 `packages/opencode/src/session/retry.ts` 和 `session/processor.ts`，继续适配为 C#：

- 408/429/500/502/503/504 和连接/读取中断支持有限重试，最多额外两次。401/403、协议错误和响应超限不重试。
- `retry-after-ms` 优先于 `Retry-After`；支持秒数、HTTP 日期以及部分网关返回的小数秒。未提供有效时间时使用 2 秒、4 秒退避；等待上限 30 秒，等待可取消。
- 流式响应一旦向用户发出文字，后续中断不重新请求，避免重复输出。工具参数仍须完整组装后才能执行。
- 修正响应读取阶段超时可能被误判为整个任务超时的问题；调用者取消仍保持取消语义。
- `ModelRetryScheduled` 事件和 `ModelRetryCount` 落盘。界面显示等待原因和时间，历史可回看。子任务次数在汇合时计入主任务一次，子任务本地仍保留自身计数。
- 传输重试不额外消耗模型编排次数，但可能产生厂商请求/计费；服务未返回的 token 用量不会臆测补齐。

主要入口：`ModelRetryPolicy.cs`、`HttpModelClient.cs`、`ModelRetryInfo.cs`、`AgentController.cs`、`BudgetedModelClient`。

### 2B：写入中断和父子任务恢复

- 新步骤记录 `WritesFiles`，既有记录按已知导出工具名兼容识别；待审批 Pending 状态不等同于已发生写入。
- 统一 `TaskInterruption` 分类：只读执行丢失标记 Interrupted/Cancelled；Started/Prepared/Committed 等未确认写入标记 NeedsReview。既有文件整理的保守复核规则保留。
- 导出 Worker 丢失也进入待复核，覆盖 Excel、Word、CSV 等生成工具，不再只关注修改原始文件的工具。
- 父任务的子任务存在未确认写入时，父任务也进入待复核，停止继续自动编排。不会把该情况当作普通子任务失败后继续交付。
- Host 通信失败时先关闭整个 Job Object，触发同组 Worker 终止，再按父子关系和会话范围分类任务。外部任务不因伪造 child ID 而被连带取消。
- 启动恢复沿用相同分类。根任务队列保留；遗留的排队子任务标记中断，不自动执行。
- 历史事件读取增加未知类型及时间戳容错，暂不认识的事件保留在数据库中、跳过显示，便于后续迭代扩展。此能力只存在于新版，不表示旧版可无条件读取新版事件。
- 已提交文件按清单、路径、长度、SHA-256 恢复成果登记；不重放原写入，不擅自将未完成任务标成成功。

这里的“恢复”指恢复事实、状态及可验证成果，不是自动从中断处续跑。恢复得到的子任务成果可从子任务记录回查，不保证自动补齐主任务聊天成果卡。

主要入口：`TaskInterruption.cs`、`LocalTaskStore.InterruptTaskTree/MarkInterruptedTasks`、`TaskHostClient`、`IsolatedTool`、`OutputRecovery`。

### 2C：成果导入幂等和缓存校验

- 交接引用由来源任务、成果路径和 SHA-256 派生；重复导入复用相同 file_id，跨 Host 重建也有效。
- 已有交接副本不重复复制，也不重复改写 manifest 和交接回执；避免反复读取成果耗尽快照配额。
- 复用前重新校验源文件及缓存副本。任一发生变化均拒绝，不用旧缓存掩盖源成果变更，也不自动覆盖被改动副本。
- 只有新增副本才占用累计文件数和快照容量，单次交接读取限制仍生效；旧版随机 ID 副本保留，首次采用新标识时可能新增一次副本。

主要入口：`ArtifactHandoff.cs`、`ImportTaskArtifactsTool.cs`、输入 manifest。

## 验证

桌面候选 Release 构建：0 警告、0 错误。旧测试工程依赖图刷新后，其构建也为 0 警告、0 错误。

| 检查组 | 数量 | 说明 |
|---|---:|---|
| 原内核专项回归 | 36 | 本地办公工具、上下文、历史和 Host/Worker |
| 调度、权限、审批专项 | 26 | 包括依赖无死锁、迟到审批隔离 |
| 多 Agent HTTP 链路 | 54 | 含读取、认证失败、实际写入、审批、依赖、篡改、复用、子任务重试 |
| 本轮重试与恢复专项 | 22 | 含实际响应体/流超时、取消、模拟提交后失联、成果恢复与事件兼容 |
| 既有 HTTP/SSE 测试用例 | 13 | HTTP 8 个、SSE 5 个 |

前四组共 138 项检查，另有 13 个兼容性测试用例。诊断脚本在执行前核对关键 DLL 与候选包的哈希，避免运行旧构建缓存。所有模型接口都由本机模拟服务提供，使用合成凭据与文件；不代表真实厂商质量、GUI 手工验收或 Win7 实机验收通过。

验证脚本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/diagnostics/native-runtime/verify.ps1 -CandidatePath C:/files/generate/gui_cli/artifacts/iteration2-candidate
powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/diagnostics/subagents/verify.ps1 -CandidatePath C:/files/generate/gui_cli/artifacts/iteration2-candidate
$env:XIAO_CANDIDATE = (Resolve-Path artifacts/iteration2-candidate).Path
python artifacts/diagnostics/subagents/http_integration.py
python artifacts/diagnostics/iteration2/http_integration.py
```

本轮新诊断工程需要先还原其已使用的 .NET 引用程序集，再运行脚本；不需要真实模型 API。验证报告随候选包 verification 目录附带。

subagents 的单元脚本与 HTTP 脚本共享诊断 bin，按上面顺序运行，不要同时启动；它们内部的子 Agent 并发仍正常验证。

## 尚未完成

未实现任意写操作自动续跑、多 Agent 共同编辑同一文件及冲突合并、厂商专有协议适配、模型上下文窗口自动识别。依然遵循上一轮的工作区权限和 2 并发/4 子任务上限。未开展 Win7 实机、长期压力、真实厂商 API 和 GUI 人工验证。

后续按 `OPENCODE_ADAPTATION_ITERATION_LOG.md` 的验收项继续推进，不能以本轮通过替代剩余验收。
