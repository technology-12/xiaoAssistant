# 晓助手 1.0.6 Windows XP 移植实现与验证（XP-B1）

日期：2026-11-08。本文记录基于 [XP 产品基线 XP-B0](../baselines/windows-xp/README.md) 完成的第一个
XP 工程实现：net40 双平台（XP SP3 x86 / XP Professional x64 SP2）独立构建、全功能同构验证与
干净便携交付。**这不是 XP 实机验收**；在真实 XP 系统完成 XP-10 验收前，不得对外声称"已支持
Windows XP"（`release_ready` 保持 false）。

> 面向独立审查者的交付报告（含逐条声明的复核命令与风险登记册）：
> [XP_PORT_1.0.6_DELIVERY_REPORT.md](XP_PORT_1.0.6_DELIVERY_REPORT.md)
>
> 独立审查（R01—R10）与其修复记录：[XP_PORT_1.0.6_REVIEW.md](XP_PORT_1.0.6_REVIEW.md)
> 及交付报告 §1；修复后验证见交付报告 §0（XP 专属回归 12/12，XP 特有失败 0）。

## 1. 交付物

| 交付物 | 位置 |
|---|---|
| XP 独立解决方案（9 工程） | `src-xp/Xiao.Xp.sln` |
| 兼容层（net40 垫片与调度） | `src-xp/WorkAssistant.Compatibility.Xp/` |
| 双平台构建输出 | `artifacts/xp-candidate/Release/{x86,x64}/` |
| 便携包 x86 | `artifacts/packages/XiaoAssistant-1.0.6-portable-windows-xp-x86.zip`（sha256 见同目录 .sha256） |
| 便携包 x64 | `artifacts/packages/XiaoAssistant-1.0.6-portable-windows-xp-x64.zip` |
| PE/依赖审计 | `artifacts/diagnostics/windows-xp/xp-package-pe-audit.json` |
| 源码词法审计（B0 工具重跑） | `artifacts/diagnostics/windows-xp/audit.{md,json}` |
| 测试证据 | `artifacts/diagnostics/windows-xp/evidence/` |

包根目录严格四文件：`晓助手.exe`、`检查运行环境.cmd`、`准备运行环境.cmd`、`首次使用说明.txt`；
其余全部在 `程序文件/`（含捆绑的微软 .NET 4 Full 离线安装器，签名校验 Valid）。

## 2. 工程结构（XP-01）

```
src-xp/
  Directory.Build.props      net40；显式 x86/x64（禁 AnyCPU）；PE 子系统 5.01/5.02；
                             net40 真实引用程序集（Microsoft.NETFramework.ReferenceAssemblies.net40 1.0.3）
  Xiao.Xp.sln                解决平台 x86/x64，禁止混装
  WorkAssistant.Compatibility.Xp/   兼容层（见 §3）
  WorkAssistant.Core.Xp/            链接 src/WorkAssistant.Core 全部源码（排除 XpRuntime.cs）
  WorkAssistant.Infrastructure.Xp/  链接主线源码 + 5 个 XP 变体
  WorkAssistant.Tools.Xp/           链接主线源码 + 4 个 XP 变体
  WorkAssistant.Host.Xp/            链接主线源码 + 1 个 XP 变体（IsolatedTool）
  WorkAssistant.OpenCode.Xp/        链接主线源码 + 1 个 XP 变体（连接存储：强制本地内核）
  WorkAssistant.Desktop.Xp/         WPF 4.0；链接主线源码与 XAML + MainWindow.Xp.xaml
  WorkAssistant.Launcher.Xp/        Program.Xp.cs（提示语改为 .NET 4 Full）
  WorkAssistant.Tests.Console.Xp/   链接主线全部测试源码（net40 目标）
```

共享源码策略与 B0 约定一致：链接 `src/` 同一批文件，只允许"双线可编译"的调用形式；
XP 专属实现集中在兼容层与变体文件。主线对应透传在 `src/WorkAssistant.Core/XpRuntime.cs`
（XP 线排除）。`scripts/Apply-DualCompileSources.py` 应用机械替换：
`Task.Run/WhenAll/WhenAny/FromResult/Delay` → `XpTask.*`、`Array.Empty` → `XpArray.Empty`、
`IReadOnlyList/Collection/Dictionary` → `IList/Collection/Dictionary`（B0 XP-001 的"改接口"方向）、
`Marshal.SizeOf<T>` → 非泛型、`new CancellationTokenSource(TimeSpan)` → `XpTask.Cts`、
带超时 `Regex.IsMatch` → `XpRegex`、`SecurityProtocolType.Tls12` → 数值 3072。

## 3. 兼容层（WorkAssistant.Compatibility.Xp）

对应 B0 §3 的接口职责，实现为命名空间兼容类型 + 扩展（共享源码零改动即可编译）：

| 模块 | 内容 |
|---|---|
| Async/AsyncInfrastructure.cs | async/await 编译器支撑：`INotifyCompletion`/`ICriticalNotifyCompletion`/`IAsyncStateMachine`/`AsyncTaskMethodBuilder(T)`/`AsyncVoidMethodBuilder`/`TaskAwaiter(T)`/`ConfiguredTaskAwaitable(T)` + `GetAwaiter/ConfigureAwait` 扩展。TCS 在 `Create()` 急切创建并跨装箱拷贝共享；**延续一律排队线程池或同步上下文，绝不内联**（补偿 net40 无 `RunContinuationsAsynchronously`，保住审批/写锁不重入的不变量） |
| Async/XpLogicalContext.cs | `AsyncLocal<T>`（不可变链 + 写时复制）；逻辑上下文在每个 await 边界与任务起点捕获/恢复（B0 禁止 ThreadStatic 替代） |
| Threading/XpTask.cs | `Run/WhenAll/WhenAny/FromResult/Delay/CompletionSource/Cts`（Timer 取消源）；`CancellationTokenSource.CancelAfter`/`SemaphoreSlim.WaitAsync` 扩展 |
| Threading/VolatileShim.cs | `System.Threading.Volatile`（MemoryBarrier 实现，语义等价） |
| IO/XpStreamExtensions.cs | `Stream.ReadAsync/WriteAsync/FlushAsync/CopyToAsync`、`StreamReader.ReadLineAsync`（APM 组合；取消即关流，与主线调用方约定一致） |
| IO/XpPipeExtensions.cs | 命名管道 `WaitForConnectionAsync/ConnectAsync`；`Dispatcher.InvokeAsync`（BeginInvoke 包装为可等待） |
| IO/Compression/ZipArchiveShim.cs | `System.IO.Compression` 家族垫片：EOCD/中央目录读取（拒绝加密、ZIP64、非 Store/Deflate），DeflateStream 解压 + CRC/大小校验；**确定性写入**（固定 1980-01-01 时间戳、保序，同输入同字节） |
| Net/HttpClientShim.cs | `System.Net.Http` 家族垫片：HttpWebRequest 传输、流式读取（SSE 按行到达）、非 2xx 按 `HttpResponseMessage` 返回、取消经 `Abort()`、`Retry-After` 解析；TLS 能力不伪装（见 §6） |
| Net/HttpListenerExtensions.cs | `HttpListener.GetContextAsync`（测试用） |
| Threading/XpRegex.cs | 无超时正则（模式固定、输入限长；net40 无超时重载，已记录） |
| WorkAssistant.Tools/XpRegex.cs（主线侧） | net48 原生 50ms 超时版本 |

## 4. XP 变体文件（Vista+ API / 无 net40 资产依赖）

| 变体 | 替换原因与 XP 实现 |
|---|---|
| `Tools/VerifiedFiles.Xp.cs` | `GetFinalPathNameByHandle`/`SetFileInformationByHandle` 为 Vista+。改为：逐级目录句柄锁定 + 重解析点拒绝 + `GetFileInformationByHandle`（卷序列+文件索引+链接数）身份复核；移动用 `MoveFileEx`（不覆盖）+ 移动后身份与哈希复核；close→move 窗口由目录锁与"不覆盖"约束缓解，复核失败按 NEEDS_REVIEW（B0 XP-009/010 的评估方向，残余风险待实机验证） |
| `Tools/AtomicTextTransaction.Xp.cs` | KTM 事务（KtmW32.dll）为 Vista+。改为同卷"临时文件 + `ReplaceFile`"提交协议：原文件哈希复核 → prepared → 唯一临时文件写入并复读校验 → staged → ReplaceFile 原子替换 → 重开复核 → committed；失败删除临时文件且原文件不变 |
| `Tools/CsvReading.Xp.cs` | CsvHelper 33.0.1 无 net40 资产。自研 RFC 4180 解析器（引号/字段内逗号换行/空行跳过/列数校验/限长），编码检测逻辑与主线一致 |
| `Tools/PdfText.Xp.cs` | PdfPig 0.1.9 无 net40 资产。恢复仓库自研纯托管解析器（2026-11-01 版）：FlateDecode（net40 DeflateStream）、literal/hex 字符串、Tj/TJ/'/" 操作符、ToUnicode CMap（含 CJK）；超限不静默截断。OCR 相关成员保留兼容默认值（XP 线无 OCR，见 §7） |
| `Infrastructure/ProjectConfigurationFiles.Xp.cs` | 去 `GetFinalPathNameByHandle`；句柄级重解析/硬链接拒绝 + 规范路径边界 |
| `Infrastructure/TaskHostClient.Xp.cs`、`Host/IsolatedTool.Xp.cs` | `GetNamedPipeClientProcessId` 为 Vista+。身份 = 随机管道名 + 当前 SID ACL + 一次性 64 位十六进制 nonce 握手 + 宿主生命周期绑定（Job + 退出清理），即 B0 §5 认可的组合设计；PID 校验不作为唯一凭据的要求本来就成立 |
| `Infrastructure/TrustedUpdates.Xp.cs` | net40 无 `AssemblyMetadataAttribute`（发布证书指纹置空 → 按主线 fail-closed 规则禁用更新安装）；`Marshal.DestroyStructure` 非泛型；X509Certificate2 非 IDisposable |
| `Infrastructure/EnvironmentReport.Xp.cs` | 组件清单按 XP 交付调整（兼容层替代 PdfPig.dll） |
| `Infrastructure/LimitedReadStream.Xp.cs` | net40 Stream 无虚 `ReadAsync`；异步读取经扩展（BeginRead→Read）仍执行上限检查 |
| `OpenCode/OpenCodeConnection.Xp.cs` | XP 基线 `remote_kernel_allowed=false`：`IsLocalOnly` 恒真，不读取开关 |
| `Desktop/MainWindow.Xp.xaml` | `VirtualizingPanel.ScrollUnit/IsVirtualizing` 为 WPF 4.5 属性；4.0 下 `CanContentScroll` 已启用虚拟化，保留 Recycling |
| `Launcher/Program.Xp.cs` | 提示语改为 .NET Framework 4 Full |

托管依赖（全部有官方 net40 资产，与 Newtonsoft 同等对待）：Newtonsoft.Json 13.0.3（net40）、
System.ValueTuple 4.5.0（net40，共享源码使用元组语法）、System.Data.SQLite.Core 1.0.119
（net40 + x86/x64 Interop）。**PE 审计确认 SQLite.Interop.dll 静态链接 CRT（无 msvcr*/ucrtbase
依赖）且无 Vista+ 导入**——这是它能加载进 XP 的强证据（最终仍以实机为准）。

## 5. 验证（同构判据）

主线 net48 在本轮验证时的源码快照存在 17 个控制台失败与 14 个 UI 断言失败（演示管线与测试
不同步的既有状态，改造前后一致——已用逆向恢复法归因）。因此 XP 线的判据是**与该主线快照完全
同构**：

| 验证 | 主线快照 net48 | XP x86 | XP x64 |
|---|---|---|---|
| 控制台用例（91 项） | 74 通过 / 17 失败 | **74 / 17（失败集合逐一相同）** | **74 / 17（相同）** |
| UI 自检断言（42 项） | 28 通过 / 14 失败 | **28 / 14** | **28 / 14** |
| PE 机器类型 / 子系统 | — | x86 / 5.01 ✓ | x64 / 5.02 ✓ |
| 包内环境检查（本机 4.8 模拟） | — | exit 0 [OK] | exit 0 [OK] |

**快照之后的演进与归因更正**：主线一度回落到 64/27。修订 1 曾将新增失败归因于并发合入的
OCR 集成——**该归因是错误的**：独立审查（R01）证明回落主因是本工作引入的
`new TimeSpan(-1)`（-1 tick 非法超时）使模型客户端构造即失败。修复后主线恢复 75/17。
OCR 集成本身对 XP 线的影响仅是排除范围（PdfOcr/PdfText 按设计排除），XP 全量 89/15、
XP 特有失败 0。最终发布的两个 zip 内是修复后的二进制（指纹见交付报告 §2）。

覆盖说明：91 用例含 CSV/XLSX/DOCX/PDF 引擎（走 XP 自研解析器与 ZIP 垫片）、SSE 流式与
重试/取消（走 HttpWebRequest 垫片 + 本地 HttpListener）、子 Agent 并发与依赖、上下文压缩、
三档权限与审批、文件整理审批、Worker 管道全链路、SQLite 状态库。UI 自检走桌面真实管道
（Desktop → TaskHostClient → WorkAssistant.Host.exe 隔离 Worker）。测试证据在
`artifacts/diagnostics/windows-xp/evidence/`。

已知非缺陷差异：XP 测试须在干净目录运行（共享输出目录中的 `app.defaults.json` 会让
SettingsStore 合并捆绑配置——主线测试目录天然没有该文件；这是环境耦合，非代码缺陷）。

## 6. 联网（对应 B0 §4）

XP 原生 Schannel 无 TLS 1.2；`(SecurityProtocolType)3072` 在 XP 上是无效请求。本实现保留
全部流式/取消/重试语义（SSE 用例已验证），证书校验不可关闭、不自动降级。联网方案按基线：
首选"仅转发模型的网关"（HTTPS 由网关终结，XP 走可信局域网或网关）；直连公共 HTTPS 在选定并
验证 XP 可用的 TLS 实现前不默认开放。

## 7. 能力差异（如实声明）

- **本地 PDF OCR 不在 XP 线**（SkiaSharp/Tesseract 无 net40 资产）；扫描件 PDF 沿用"明确提示
  不支持"，`PdfTextResult` 的 OCR 成员返回兼容默认值。
- **远程 OpenCode 内核强制关闭**（基线边界），UI 侧栏相应隐藏。
- **更新安装禁用**（未配置发布证书，fail-closed，与主线未签名构建一致）。
- 正则启发式校对在 net40 无超时上限（模式固定、输入 ≤4000 字符；主线 50ms）。
- XLSX/DOCX/PPTX/CSV/PDF/SQLite 全部本地能力保留；三档权限、子 Agent、上下文压缩、检查点
  恢复、项目/技能/工作区全部保留（91 用例同构验证）。

## 8. XP 工作包状态

| 编号 | 状态 | 说明 |
|---|---|---|
| XP-01 独立 net40 双平台方案 | **本轮完成** | 真实 net40 引用程序集构建；PE/程序集审计通过；非空壳（全部功能链接编译） |
| XP-02 调度/上下文/取消适配 | **本轮完成** | 兼容层；并发子 Agent 上下文隔离经测试验证 |
| XP-03 XP 模型传输层 | **本轮完成** | HttpWebRequest 传输；SSE/超时/取消/重试用例通过；网关配置沿用 Profile 机制 |
| XP-04 管道认证与进程隔离 | **本轮完成（设计变更见 §4）** | nonce+ACL 身份；抢连/重放等攻击面验证仍待实机 |
| XP-05 文件边界/锁/原子提交 | **本轮完成（协议变更见 §4）** | ReplaceFile/MoveFileEx + 身份复核；竞争窗口残余风险已记录，待实机验证 |
| XP-06 ZIP/CSV/PDF/SQLite 移植 | **本轮完成** | 自研 ZIP/CSV/PDF + 官方 net40 资产；全部用例同构 |
| XP-07 完整串联 | **本轮完成** | 与主线同构的 91 用例全链路 |
| XP-08 WPF 4.0 界面 | **本轮完成（开发机）** | XAML 4.0 兼容；1024×768/高 DPI/中文字体在 XP 上的表现待实机 |
| XP-09 技能/运行环境/干净包 | **本轮完成** | 技能经 Content 复制随包；.NET 4 Full 准备流程；根目录四文件、无用户数据 |
| XP-10 实机验收与发布门禁 | **未开始** | 18 项门槛全部保持 not-validated；`release_ready=false` |

## 9. 后续（进入 XP-10 的入口）

1. 准备 XP SP3 x86 与 XP x64 SP2 实机/虚拟机（.NET 4 Full + 补丁清单），按 B0 §7 矩阵采集
   证据到 `artifacts/diagnostics/windows-xp/`。
2. 重点复核：SSE 长流跨包中文、ReplaceFile/MoveFileEx 在各卷的竞争窗口、SQLite.Interop 实机
   加载、WPF 低配置渲染与中文字体、高 DPI。
3. 2 GB 验收机建立性能实测表后再设阈值。
4. 两平台都通过后更新 `baseline.json` 的 evidence 与 `release_ready`（审计器 `--check` 仍返回 2
   属预期，尚无实机证据校验器）。

## 10. 复现命令

```bash
export DOTNET_ROOT="$USERPROFILE/.dotnet"; export PATH="$USERPROFILE/.dotnet:$PATH"
dotnet build src-xp/Xiao.Xp.sln -c Release -p:Platform=x86
dotnet build src-xp/Xiao.Xp.sln -c Release -p:Platform=x64
# 测试须在干净目录（见 §5 已知非缺陷差异）
python scripts/Build-PortableRelease_1_0_6_Xp.py
python scripts/Audit-XpPackage.py        # PE/依赖审计
python scripts/Audit-WindowsXpBaseline.py  # B0 词法审计（会显示 B0 指纹漂移：共享源码演进，属预期）
```
