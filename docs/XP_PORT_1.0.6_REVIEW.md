# XP 1.0.6 交付独立审查

审查日期：2026-11-08。对象：`XP_PORT_1.0.6_DELIVERY_REPORT.md`、XP-B0 设计及当前源码、两个已交付 XP ZIP，以及主线 1.0.6 的模型客户端初始化。结论：**不通过完整设计验收。已建立可构建的双平台候选工程，但 XP-02～XP-09 仍有实现缺陷或证据缺口，不能认定仅剩 XP-10。**

本轮没有修改业务源码、交付报告、产品基线或发行包；只新增本审查文档及隔离诊断产物。构建写入独立输出目录和常规 obj 缓存。测试仅使用自建文件、目录链接及本机回环 HTTP，不调用真实模型 API、不更改用户设置或聊天历史。

## 1. 可认可的交付

- 独立 `src-xp/Xiao.Xp.sln` 确实存在；重新还原并构建 x86、x64，两者均 **0 警告、0 错误**，目标为 net40，使用 net40 引用程序集。
- 两个 XP ZIP 确实存在，大小、SHA-256 与交付报告一致：x86 为 52,351,661 字节，`c6e1d597aa7a2b1aeff592cadb458f047194cc23c3a109659b50f95b9ed1d97a`；x64 为 52,526,751 字节，`e6062e69eac2ca4489438b8704f983fe6c9d1fe0de69678d9800823b98a6b265`。
- 分离兼容层、共享源码链接、两平台独立入口、四文件根目录、Full 运行环境准备等工程结构已落地；可以继续在此基础上修复。
- 报告如实注明未进行真实 XP 验收，`release_ready=false` 正确，应继续保持。现代机器上的执行不能替代 XP SP3 x86 / XP Professional x64 SP2 的验收。

## 2. 必须修复的问题

### R01 [P1] 无限超时替换错误，已经破坏正式主线模型连接

位置：`src/WorkAssistant.Infrastructure/HttpModelClient.cs:94`、`AnthropicModelClient.cs:26`。报告 §3.1 将 `Timeout.InfiniteTimeSpan → new TimeSpan(-1)` 认定为等值替换，这是错误的。

`TimeSpan(long)` 的单位是 tick，`new TimeSpan(-1)` 为负一个 tick；无限超时为负一毫秒。原生 HttpClient 会拒绝这个值，参考 [Microsoft HttpClient.Timeout 契约](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.timeout)。

独立探针没有发起网络请求，仅构造模型客户端：

| 对照对象 | OpenAI Chat | Anthropic Messages |
|---|---|---|
| 当前主线源码独立构建 | ArgumentOutOfRangeException | ArgumentOutOfRangeException |
| 已交付的主线 1.0.6 包 | 同样失败 | 同样失败 |
| 已交付的 1.0.5 包 | 构造成功 | 构造成功 |

影响：用户尚未访问 API 就会失败。该项也进入了此前 OCR 正式包；此前 193 项检查没有覆盖真实客户端的初始化路径，不能以这些通过数排除此回归。报告 §6 将主线新增失败归因于 OCR，缺少依据，必须重新归因。

要求：使用两个框架均正确支持的无限超时表达式或平台适配值；补齐两个协议的“构造即验证”回归，再测实际模型请求。该问题优先于进一步 XP 扩展。

### R02 [P1] 自制 AsyncLocal 没有保持异步调用上下文隔离

位置：`src-xp/WorkAssistant.Compatibility.Xp/Async/XpLogicalContext.cs:51`；`AsyncInfrastructure.cs:134`、`:175`。当前以 ThreadStatic 保存链头，在 await/Run 处手动流转，但 builder.Start 直接执行 MoveNext，没有隔离和恢复调用方上下文。

复现：父调用设置值为 `parent`，调用一个异步子方法；子方法在首次 await 前改为 `child`。子方法尚未完成，父调用立即读到 **child**。原生 .NET 4.8 同一控制用例仍为 **parent**。

影响：DocumentLimits 的取消令牌、计时器、工具名、检查点/进度回调等不能据此认定已按 Agent 隔离。问题是上下文边界不完整，不能仅把 ThreadStatic 换个名称解决。报告“逻辑上下文等价”“零语义改动”的结论不成立。

同一兼容层还将异步方法中的 OperationCanceledException 放进 TrySetException，实测返回任务为 **Faulted**，原生结果为 **Canceled**。取消状态语义也需一起修正。

要求：正确处理异步方法入口、挂起、恢复、异常和退出的上下文边界；验证父子、兄弟 Agent、同步完成 await、线程切换及取消的交叉组合。

### R03 [P1] WhenAll 在一个任务失败时提前结束

位置：`src-xp/WorkAssistant.Compatibility.Xp/Threading/XpTask.cs:112`、`:145`。发现一个任务 Faulted/Canceled 即完成聚合 TCS，其余任务不必结束。它不是原生 WhenAll 的行为。

泛型重载复现：两个任务中一个故障、另一个仍 pending；等待短暂调度后，XP WhenAll 已完成，原生 WhenAll 尚未完成。两个 XP 平台结果一致。非泛型重载具有相同的提前完成分支。

影响：调用方可能在其他子任务仍运行/写文件时就退出聚合、释放协调器或处理失败。异常回调、持久化失败等情况下，不能以“子任务通常捕获异常”作为安全保证。

要求：等待所有输入任务终结，再按异常、取消、成功的规则汇总；覆盖兄弟任务故障后仍在写入、finished 回调失败和取消场景。

### R04 [P1] HTTP、管道和流的取消注册被提前释放

位置：`Net/HttpClientShim.cs:136`、`IO/XpPipeExtensions.cs:20`、`IO/XpStreamExtensions.cs:23` 与 `:40`，均位于 `src-xp/WorkAssistant.Compatibility.Xp/`。

这些普通返回 Task 的方法使用 `using var registration`，发起 APM 后立即 return。此时方法退出，取消回调也被 Dispose，而底层异步 I/O 尚未结束。

已在两个 XP 包的兼容层中复现：

- 异步管道等待在调用后取消，150 ms 后仍未完成。
- 本机延迟响应 HTTP 在发起后取消，150 ms 后任务仍 pending，IsCanceled 为 false，随后继续收到响应。

要求：注册存活到实际操作结束，并处理取消、完成、异常之间的竞争。补测发起前取消、连接中取消、等待响应头时取消及 SSE 中途取消。现有父进程杀 Job 的兜底不能证明传输层自身正确。

另需核对纯 HTTP 网关的初始化：共享模型客户端仍无条件设置 TLS 1.2 数值。XP 原生 4.0/Schannel 路径未在本轮实际运行，不能据此认定“填入 HTTP IP 就能使用”；应在目标环境补证并避免网关请求被 TLS 初始化阻断。

### R05 [P1] 已校验并批准的文件仍可能覆盖后来的修改

位置：`src-xp/WorkAssistant.Tools/AtomicTextTransaction.Xp.cs:57`—`:79`。原文件哈希校验后即关闭文件；prepared、临时写入、staged 完成后直接 ReplaceFile，没有保证被替换对象仍是批准的版本。

确定性复现：在提供的 staged 回调处模拟另一个编辑器写入 `unapproved concurrent change`，然后继续提交。最终文件变为 **agent replacement**，操作没有报版本冲突，后来的修改丢失。

这不是只需真机测量的性能风险。报告登记 R2 并不能使其满足 B0 的“批准绑定具体版本、并发修改不丢更新”要求。

要求：重新设计可验证的排他与版本绑定提交协议；无法保持边界时应拒绝自动提交并保留候选，不能依赖替换后的哈希检查发现此前已被覆盖的数据。

### R06 [P1] VerifiedFiles.OpenRead 可经目录链接读取授权根之外

位置：`src-xp/WorkAssistant.Tools/VerifiedFiles.Xp.cs:133`。FileStream 跟随父目录 junction 打开实际目标后，只检查文件自身属性/硬链接及原始路径字符串前缀，没有锁定并验证父目录链或确认实际对象位于授权根内。

复现只使用审查目录中的自建文件：`permitted/link` 是指向同一测试目录下 `outside` 的 junction。调用 OpenRead 读取 `permitted/link/marker.txt`，成功读到授权根外的标记，未抛异常。x86/x64 相同。

要求：恢复目录链和实际文件身份的边界约束，覆盖既有链接及检查后替换竞争；不能以最终目标文件本身不是重解析点替代路径链验证。

### R07 [P1] 普通多页 PDF 被重复提取，正文与页码不可信

位置：`src-xp/WorkAssistant.Tools/PdfText.Xp.cs:123`。页循环外误套 `foreach (var kv in streams)`，每个内容流都会再遍历全部页面。

用独立生成、正常可读的两页纯文本 PDF 复现：声明页数为 **2**，返回正文页数为 **4**，页码依次 **[1,2,1,2]**。不涉及扫描件或 OCR，两个平台均复现。实际源码还保留 `[diag]` 输出，与报告声称“去调试输出”不符。

此外，代码按文件对象出现顺序代替页树顺序、把全部字体 CMap 合并；这些是实质性解析能力退化，尚未证明满足中文文档的可靠读取要求。

要求：先修复重复循环，再补齐正确页树顺序、按字体解码及不支持流的显式覆盖范围；用多页、多字体中文、重排页面等文档验证，不能仅用简单单页 PDF 证明等价。

### R08 [P2] 包内宣称自动 OCR，但 XP 实际排除了 OCR

报告明确披露 XP 不含 OCR，这一点本身没有隐藏。但检查两个实际 ZIP，内置 `skills/pdf-extract` 仍为 1.2，SKILL.md 明确告诉模型“扫描页自动本地 OCR”；共享读取工具的说明和零字错误信息也仍声称进行了本地 OCR。

因此“扫描件会清晰提示不支持”的报告声明与实际交付不一致。最初 B0 没有要求 OCR，但若交付范围是保留当前 1.0.6 的全部功能，OCR 是需要明确处理的新增差异，不能自行当作全功能等价。

要求：有统一的 XP 能力清单，驱动工具说明、技能、界面与错误信息；若要求 1.0.6 全功能，则补实现本地 OCR，否则明确约定差异，不得继续向模型虚报能力。

### R09 [P2] PE 审计会对缺失交付返回成功

位置：`scripts/Audit-XpPackage.py`。给它一个真实存在但为空的 candidate 目录，结果为 x86=0、x64=0、problems=[]，**退出码仍为 0**。此外，Vista+ 导入的 flagged 值只写报告，未加入 problems，不能据“exit 0”推导完整依赖通过。

要求：目标平台缺失、必要入口/依赖缺失、解析失败、位数/子系统不符均应失败；导入与运行时依赖审查需要明确覆盖范围。应审计实际 ZIP 的最终载荷并绑定散列。该脚本目前可用作结构观察，不能作为已完成发布门禁的证明。

### R10 [P2] 交付的迁移脚本与实际兼容 API 不一致

位置：`scripts/Apply-DualCompileSources.py:16`—`:25`。在内存中调用 transform（没有改写源码）：`Task.Run` 被转换成 `Xp.Tasks.Run`，`Array.Empty` 被转换成 `Xp.Arrays.Empty`。实际工程提供的是 `XpTask` / `XpArray`，没有对应的 Xp 命名空间 API。

要求：修正脚本并用独立源快照重放验证。不能把该脚本列为已可复现、可逆的交付依据；尤其不要让其他工具按报告 §8 直接对正式源码执行它。

## 3. 工作包判定

这里区分“工程代码存在”和“满足设计且可验收”；没有把待真机验收的项目当作已通过。

| 工作包 | 本次审查判定 | 主要原因 |
|---|---|---|
| XP-01 构建与依赖入口 | 构建基础基本完成 | 已独立验证两平台 net40 构建，包指纹相符；完整目标环境验收仍待完成 |
| XP-02 异步与执行上下文 | 不通过 | R02、R03，取消状态也与原生不同 |
| XP-03 模型传输 | 不通过 | R04；真实 XP 的 HTTP 网关/两协议/SSE 尚无验收证据 |
| XP-04 Worker 与 IPC | 部分实现，待修复 | ACL、nonce、Job 等结构存在；取消缺陷已复现，抢连/重放等要求缺证据 |
| XP-05 文件边界与提交 | 不通过 | R05、R06，影响数据和授权边界 |
| XP-06 Office/PDF/CSV/SQLite | 不通过整体能力验收 | R07；大文档、中文复杂 PDF 和目标机原生依赖验收未齐 |
| XP-07 完整智能体串联 | 待修复后重验 | 依赖 XP-02～06；共享代码能编译不等于权限/子任务/恢复链路已满足要求 |
| XP-08 WPF 界面 | 部分实现，待验证 | 已有 4.0 变体；28/42 的旧断言结果不能替代 XP 的交互、DPI、字体和低配验收 |
| XP-09 技能与干净交付 | 部分完成 | 双包结构可认可；R08、R09、R10，技能能力与工具不一致、门禁及复现流程不足 |
| XP-10 XP 双平台验收 | 未完成 | 报告已如实披露，没有真实 XP 环境证据 |

## 4. 对交付报告结论的修订建议

可以保留“建立 XP-B1 候选工程”“双平台 net40 构建通过”“尚未宣称兼容 XP”。应撤回或降级以下结论：

1. “XP-01～XP-09 工程实现完成”——至少上述存在实际功能缺陷的工作包尚未完成。
2. “约 1.7 万行共享源码零语义改动”“兼容层等价”——独立对照已出现相反结果。
3. “主线新增失败由 OCR 引起”——模型初始化回归与错误超时表达式直接相关，不能按并发工作流推断责任和原因。
4. “测试结果同构即可完成验收”——共同失败需要逐项解释，必要场景要有新的有效验收；两份实现同样失败不能证明要求已实现。91 项旧控制台及 42 条 UI 断言还漏掉本次复现的核心场景。
5. “文件风险等实机再确认”——越界读取和丢更新已经在当前环境确定复现，不属于只差实机观察的风险。

保留 release_ready=false。建议按 R01 → R02/R03/R04 → R05/R06 → R07/R08 → R09/R10 的顺序修复，并补回归；再安排原设计要求的真实 XP 两平台验收。

## 5. 可复核证据

隔离目录：`artifacts/diagnostics/xp-review/`。

- `review-summary.json`：源码散列、构建结论、两个平台的最小复现结果。
- `review-result-x86.json`、`review-result-x64.json`：两平台实际包的相同缺陷结果。
- `probe/Program.cs`、`probe/ReviewProbe.csproj`：引用实际 XP 包程序集、针对 net40 编译的最小探针。
- `native-semantics-result.txt`、`NativeSemanticsProbe.cs`：原生 .NET 4.8 对照，分别为 WhenAll 未提前完成、父上下文 parent、取消状态 Canceled。
- `constructor-result.txt`、`MainlineCtorProbe.cs`：当前源码、主线 1.0.6 及 1.0.5 的两个协议构造器对照；没有真实 API 调用。
- `empty-audit-result/xp-package-pe-audit.json`：空候选仍成功的审计结果。
- `fixtures/`：自建 PDF、版本冲突和目录链接复现文件；其中 outside 只是本审查目录中授权根之外的自建子目录，不含真实用户文件。

运行环境为 Windows 10.0.26200 / CLR 4.8。探针对 net40 引用程序集编译，但运行环境仍是现代 CLR，因此这些结果用于证明平台无关的实现缺陷，**不作为 XP 实机通过证据**。本次没有重新运行产品 UI 自检以避免接触实际用户配置，也没有把旧报告通过数当作本次通过数。
