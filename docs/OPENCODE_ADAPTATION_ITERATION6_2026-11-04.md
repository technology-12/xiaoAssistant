# 第六轮：文本提案审批、事务提交与条件回滚

日期：2026-11-04。候选入口：`C:/files/generate/gui_cli/artifacts/iteration6-candidate/WorkAssistant.Desktop.exe`。第五轮目录保留。本轮继续沿用本地 C# Host、隔离 Worker、WPF/.NET 4.8；未增加远程内核或后台网络执行服务。

## 本轮交付

新增 `apply_text_edit` 与 `rollback_text_edit`，均只作用于本任务 `plan_text_edit` 生成的既有输入文本提案。先生成差异和候选，再经当前用户确认，事务内重新检查原文版本并提交；回滚需要另一次确认，只允许原文仍处于该提案提交后的版本时恢复备份。

两个工具均要求 plan_id、proposal_fingerprint、expected_sha256。提交使用提案的 base_sha256，回滚使用 candidate_sha256。模型参数中不存在“已批准”开关；伪造或附加此类字段会被拒绝。

审批展示实际源路径、提案指纹、原文 SHA-256 与完整差异，回滚明确标注反向恢复。Host 在批准后创建进程内授权凭证，绑定任务、工作区、调用、工具、规范化参数摘要、提案和计划状态；通过已认证的本机命名管道传给隔离 Worker。Worker 再次验证提案内部内容及凭证。审批期间更换原文或提案会使批准失效；工作区/角色/技能、只读及只规划模式继续约束这些工具。

ToolContext、ApprovalRequest 和 FilePlanRecord 原有构造入口保留。新增字段为可选的宿主状态，不由模型注入。桌面仍使用现有审批窗口；未设置无人值守自动批准。

## 提交底层与实际支持范围

采用 Windows KTM/Transactional NTFS（TxF）进行**事务内容更新**，不是先删除再移动，也不是普通 File.WriteAllText 覆盖。事务内打开原文件的读写句柄，禁止并行访问，核对最终路径、单硬链接身份和原文字节哈希，再暂存并校验候选。关闭事务文件句柄后调用 CommitTransaction，使内容更新整体生效。过程不更换原文件对象。

选择该路径是为了给 Win7 目标提供事务隔离，防止“先检查、后普通覆盖”之间的外部更新竞态。不支持的系统/卷、远程文件系统及相关错误返回 `ATOMIC_WRITE_UNAVAILABLE`；不会暗中降级成非事务覆盖。具体系统返回值保留在错误中，便于定位。目标被其他进程独占、已变化或事务冲突时返回 `EDIT_CONFLICT`。

**本轮实际提交需要可用的本地 NTFS 事务支持。** 不是对所有 Windows/文件系统的通用写入兼容声明。微软已不推荐新应用依赖 TxF，并提示未来 Windows 可能不再提供；此后端隔离在 AtomicTextTransaction 中，后续替代必须继续满足隔离和故障语义，不能只换成存在竞态的普通覆盖。

依据：[CreateFileTransactedW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createfiletransactedw)、[How to Use Transactional NTFS](https://learn.microsoft.com/en-us/windows/win32/fileio/how-to-use-transactional-ntfs)、[Programming Considerations](https://learn.microsoft.com/en-us/windows/win32/fileio/programming-considerations-for-transacted-fileio-)。Win7 实机与不同卷配置仍须独立验收；当前验证使用本机实际可用的事务能力。

## 持久化与故障处理

1. 提案和批准校验通过后，先把精确原始字节保存为任务备份成果，写入并校验提交清单。
2. 事务内确认原文基线；保存计划的 prepared 状态和独立意图 journal，并等待 Host 将检查点持久化确认。
3. 暂存候选、核对事务内字节；保存 Staged 意图。此时原文件未完成提交。
4. CommitTransaction 成功后保存 Committed 意图，再核验事务外当前文件。
5. 保存提交回执、回执 SHA-256 和最终计划状态，标记 Verified。回滚使用独立备份、journal 和回执。

事务提交前终止 Worker，未提交改动由事务撤销；事务提交后终止 Worker，已提交内容保留。应用侧未确认的 prepared/committed 状态进入 NeedsReview，保留证据，不根据“文件看起来一样”自动宣布完成或重放。备份/回执缺失、被改动、提交未确认或提交后又发生外部更新，也不自动补写或回滚。

计划状态为 planned → edit-prepared → edit-applied，回滚为 rollback-prepared → edit-rolled-back；不确定时为 edit-needs-review。回滚完成后不能用旧提案再次提交。新的修改需要新任务、新快照和新提案。

重复使用**新调用和新批准**操作已经完成的计划时，只核验当前版本、备份和回执，再返回 already_applied，不再次写入。原 tool_call_id 的缓存重放不直接复用历史成功状态，返回 NeedsReview 并提示用 validate_text_edit 回查事实，避免把旧回执当成当前文件仍未变化的证明。

validate_text_edit 现按计划状态核验：未提交/已回滚对照基线，已提交对照候选；不确定状态不能仅靠文件哈希声明成功。只读核验仍不构成下一次写入授权。

## 使用方式

可将示例目录中的 `.claude/commands/apply-edit.md` 放入工作区，再输入：

```text
/apply-edit 把 text-edit-demo.txt 中的“待核对”改为“已核对”
```

模型先生成提案，再请求当前批准。仅需方案时继续使用 `/propose-edit ...`；“只生成文本编辑提案”“仅交付差异”“不要提交”等请求会抑制原文写入工具。继续修改已提交文件，需要新建一轮以重新捕获输入快照。

模型可在同一任务内按用户要求调用 rollback_text_edit，提供本任务提案和候选哈希，再次审批后恢复。**当前不支持在新任务/新聊天中直接拿上一任务 plan_id 回滚**；会话级撤销和 Git 级回退按第 10/12 轮推进。本轮没有新增全局撤销按钮，不将内部工具接口冒充跨任务撤销功能。

权限和范围仍限于 inputs 下已有受控文本，前后最多 256 KiB。没有开放任意源码树、多文件提交、文件新建/删除，也没有扩大子 Agent 的原文写入权限。项目源码访问在第 7 轮、多文件变更在第 8 轮、并发编辑合并在第 11 轮继续实施。

## 代码交接

| 源码 | 职责 |
|---|---|
| Core/TextEditApproval.cs | 审批快照、参数绑定、任务归属与宿主凭证 |
| Core/AgentController.cs | 审批内容、批准后授予单次调用凭证 |
| Host/Program.cs、IsolatedTool.cs | 本地调用和隔离 Worker 的授权传递 |
| Tools/AtomicTextTransaction.cs | 目录/句柄验证、事务内基线核验、暂存、提交与回滚取消 |
| Tools/CommitTextEditTool.cs | 提交/条件回滚、备份/回执/journal、幂等与待复核处理 |
| Core/Interfaces.cs、ToolContracts.cs | 可选授权、回执摘要与计划状态，保持旧构造入口 |
| Core/ModelProtocol.cs、TaskInterruption.cs | 将两个工具纳入修改类、只读约束与中断分类 |
| Core/ToolDispatcher.cs | 防止文本写入缓存重放绕过当前状态核验 |
| Tools/TextEditTools.cs、Core/SystemPrompts.cs | 状态感知的只读验证、提交提示与只提案模式 |

## 验证

Release 构建 0 警告、0 错误。本轮 50 项检查覆盖授权缺失、审批内容/任务绑定、真实事务提交与回滚、重复调用、只读/规划约束、批准期间原文/提案变化、独占写入、后续更新、备份/回执篡改和备份缺失。两个真实辅助进程分别在事务暂存后及提交后被强制终止，核验原文字节、持久备份和拒绝自动重放。实际 Host/Worker 通过合成本机 API 验证批准、拒绝、回滚和冲突；现有审批窗口进行无窗口 WPF 渲染并检查关闭即拒绝。另以合成不可用后端验证：实际提交失败时，不能因提案成果存在而标为提交完成，也不继续模型重试。

前五轮的 294 项检查继续回归，本轮合计 344 项。各报告在候选包 verification 中；合成模型和测试文件没有使用真实用户凭据或业务材料。复现入口：`python artifacts/diagnostics/iteration6/http_integration.py`。其余诊断通过 XIAO_CANDIDATE/CandidatePath 指定本候选。

这不是断电实测、Win7 实机、真实模型服务或全部文件系统验收。NTFS 事务不可用时明确失败的分支已实现，尚未覆盖所有不可用卷组合。下一轮按路线图推进授权项目树的 read/ls/glob/grep，同时继续保留本轮单文件提交边界。
