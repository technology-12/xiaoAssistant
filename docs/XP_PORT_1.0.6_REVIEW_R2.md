# XP 1.0.6 修订 2 独立复审

日期：2026-11-08。对象：`XP_PORT_1.0.6_DELIVERY_REPORT.md` 修订 2、更新源码、两个更新的 XP ZIP，以及现有主线 1.0.6 发行包。

结论：**修复有实质进展，但本轮交付仍不通过。** 多项源码缺陷已经修复，x86 包包含相关改动；x64 ZIP 却保留了上一轮的核心 DLL。另有 PDF 字体解码、审计失败路径及交付证据一致性等遗留。不能把“源码回归通过”换算成“两个 ZIP 均修复”。

本轮仅新增审查文档和隔离诊断产物，没有修改业务源码、基线、交付报告或发行包。构建输出位于 `artifacts/diagnostics/xp-review-r2/`；obj 常规构建缓存有更新。

## 1. 已验证的改善

- 当前源码的 x86、x64 net40 解决方案都可独立构建：0 错误，各有一个未使用变量 `writing` 的警告，非阻断问题。
- 原上下文泄漏、WhenAll 提前完成、async 取消状态错误，在更新源码和 x86 包中不再复现。
- 原管道/HTTP 取消后一直 pending 的用例已修正；真实回环 TCP 测试确认取消能使操作终结。HTTP 任务当前以 Faulted 终结，不应据此声称与原生取消状态完全等价。
- 原目录链接越界读取在更新源码和 x86 包中已被拒绝。
- 原两页 PDF 返回四页的问题在更新源码和 x86 包中已修正。
- R05 的并发写者覆盖用例已修正：独占写入期间其他写者被拒绝。
- 新增 12 个 XP 专属回归已对独立构建的 x86/x64 源码产物运行。两平台各有 11 个在沙箱中通过；HTTP.sys 用例在沙箱中报 PlatformNotSupportedException，移出沙箱后各自通过。因此 12 个不同用例均通过，HTTP 的环境差异不计为产品缺陷。
- PDF 技能文本在两个 ZIP 中均更新为 `1.2-xp`，不再声明扫描件 OCR；源码能力开关和 x86 工具描述也已一致。
- 审计器现在会拒绝空候选/缺平台目录；迁移脚本的 XpTask/XpArray 命名已修正，check 与内存替换样例通过。

上述测试在 Windows 10.0.26200 / CLR 4.8 运行，探针针对 net40 引用集编译；仍不是真实 XP 验收。

## 2. 阻断交付或需要继续修复的问题

### S01 [P1] 新 x64 ZIP 保留旧核心 DLL，修复没有进入交付包

本轮检查的是报告修订 2 指定的两个 ZIP，指纹完全吻合：

| 包 | SHA-256 |
|---|---|
| x86 | `39acbc54c54c9772a31f6620c4e514e6891904247df6b5499ed0a7a5773909db` |
| x64 | `4c489c74849d1ca9c282816093bfb68593ffaa0b814871633b70d377c3dc7799` |

已将 ZIP 内 DLL 与解压目录逐一核对，确保没有拿错解压副本。再对照上一轮审查保存的 DLL：

| 组件 | 新 x86 包与旧审查副本 | 新 x64 包与旧审查副本 |
|---|---|---|
| WorkAssistant.Core.dll | 已变化 | **SHA-256 完全相同** |
| WorkAssistant.Tools.dll | 已变化 | **SHA-256 完全相同** |
| WorkAssistant.Compatibility.Xp.dll | 已变化 | **SHA-256 完全相同** |

对新 x64 ZIP 实际 DLL 的独立复现结果仍是：

- 子方法启动后父上下文为 `child`，未保持 `parent`。
- WhenAll 在兄弟任务仍 pending 时提前完成。
- 异步取消状态仍为 Faulted。
- 管道及 HTTP 取消后仍 pending。
- 两页 PDF 返回四页 `[1,2,1,2]`，仍有旧 `[diag]` 输出。
- 并发修改仍能被覆盖，目录链接仍能绕过读取边界。
- 没有新的 OfficeCapabilities 类型，工具描述仍宣称 OCR；尽管包内技能已改成“不支持 OCR”。

独立从当前源码构建的 x64 产物则通过上述原复现用例。这说明源码与包没有同步，不能依据源码测试结果为 ZIP 背书。

位置：`scripts/Build-PortableRelease_1_0_6_Xp.py:69`、`:76` 直接复制已有候选目录，没有强制绑定被测试构建的散列。其 PARITY 在 `:37` 仍硬编码旧的 74/17，而非本轮报告中的 89/15；包内测试证据也应改为从实际报告生成。

要求：从同一冻结源码快照重新构建两平台，测试该份确切二进制，再打包并验证包内 DLL 散列与测试产物一致。给每次修订独立 build id/版本或不可混淆的包名；不要仅更新技能/说明后继续沿用旧 DLL。

### S02 [P1] 主线源码已修复 R01，但正式 1.0.6 ZIP 尚未更新

源码中的无限超时值已改为 `TimeSpan.FromMilliseconds(-1)`。独立构建当前主线 Infrastructure 后，两个协议的客户端都能构造成功。

但现有 `artifacts/packages/XiaoAssistant-1.0.6-portable.zip` 的散列仍为：

`d0142cb9d54936eca16882924f0c1553b1b99353b2e7f6b803d6e99ea1f78563`

这是上一轮有问题的原包。已核对 ZIP 内 Infrastructure.dll 与用于测试的解压副本一致；OpenAI Chat 和 Anthropic Messages 客户端仍均抛 ArgumentOutOfRangeException。

要求：主线正式包也需要重新构建、验证并交付修复版本。此项是此前主线回归的交付遗留，不能因为 XP 源码已改就认定现有正式下载包已修复。未调用真实模型 API，本测试只构造客户端。

### S03 [P1] PDF 字体解码仍会污染普通西文文字

位置：`src-xp/WorkAssistant.Tools/PdfText.Xp.cs:456`、`:499`、`:636`。没有 ToUnicode 映射的字体被排除在 pageFonts 之外；内容流切换到该字体时，没有清除上一字体的映射。此前的全局映射回退也不能作为其他字体的正确映射。

本轮构造普通一页 PDF：

- 使用 STSong-Light Type0 字体书写“甲”，该字体具有合法 ToUnicode 映射。
- 同页切换到普通 Helvetica，书写西文 `u2`，Helvetica 没有自定义 ToUnicode。

独立 pypdf 和主线 1.0.5 的 PdfPig 两个控制组均读取为 **甲u2**（忽略排版换行）。新 x86 包与当前源码独立构建的 x64 都读成 **甲甲**。没有报错，也没有标为部分覆盖；这是正文内容被静默改写，不能只归类为排版差异。

要求：每次 Tf 切换都建立当前字体的正确解码状态；无自定义 CMap 时使用该字体的标准/指定 Encoding，不能沿用前字体或其他字体的 CMap。补测 CJK + Latin、多字体、无 ToUnicode 字体及多内容流组合。

### S04 [P2] 审计器仍对损坏的必需 Host.exe 返回成功

位置：`scripts/Audit-XpPackage.py:149`—`:150`。parse_pe 返回 None 时仍直接 continue。必需文件检查只证明文件存在，不能证明它是可执行文件。

在隔离副本中保留其他完整载荷，仅将 x86 的 WorkAssistant.Host.exe 改为文本字节 `NOT A WINDOWS EXECUTABLE`，审计结果仍为 problems=[]、**exit 0**。这个 Host 不可能正常启动。

空目录失败路径的修复成立，但 R09 的“PE 解析失败均计失败”尚未完整实现。

要求：必需二进制不可解析时失败；对所有声明分发的 EXE/DLL，None 也应是失败，不能静默跳过。对最终 ZIP 加缺失、损坏、错误位数和错误依赖的负例，绑定被测试载荷的散列。

### S05 [P2] 启用系统代理后，XP HTTP 适配层仍把代理清空

位置：`src-xp/WorkAssistant.Compatibility.Xp/Net/HttpClientShim.cs:184`—`:186`。

UseProxy=true、handler.Proxy=null 是采用系统默认代理的常见情况，当前分支却将 request.Proxy 设为 null。显式传 handler.Proxy 时该分支又不执行，指定代理同样没有正确赋给请求。

独立测试在当前进程设置一个仅作标记的默认 WebProxy，构造请求但不发送，结果 system_proxy_preserved=False。会影响需要系统代理的模型访问。此问题在旧实现中也存在，本轮扩展检查确认尚未处理。

要求：关闭代理、使用系统代理、使用指定代理三条路径分开实现；补配置到请求对象的验证，不必访问真实外网即可覆盖。

### S06 [P2] 新的只读 OpenRead 会创建目录

位置：`src-xp/WorkAssistant.Tools/VerifiedFiles.Xp.cs:62`、`:143`。OpenRead 复用了会调用 Directory.CreateDirectory 的 LockAncestors。

对授权根下不存在的 `read-created-<随机值>/sub/missing.txt` 调用 OpenRead，文件读取失败，但两个父目录已被创建。新 x86 包和当前源码独立构建的 x64 都有该副作用。

要求：只读路径只打开并验证已有目录；不存在则失败。目标目录创建应留在有写入意图的路径中，不与只读校验共用创建分支。

### S07 [P2，静态检查] snapshot 辅助脚本缺少防误删边界

位置：`scripts/Apply-DualCompileSources.py:105`—`:107`。新增 snapshot 模式会对已经存在的 --target 无条件 shutil.rmtree，没有拒绝源码根、源码祖先目录或已有非快照目录。

本轮没有执行覆盖/删除测试，没有触及任何实际目录。按静态代码即可确认该入口缺少保护；迁移命名修复可以认可，仍建议在交给其他工具执行前补充边界验证和已有目录保护。不能让“生成验证副本”误删源工程或其他文件。

## 3. R05 的设计差异仍须独立验收

原先“校验后关闭句柄，再覆盖并发修改”的具体缺陷已修正，本轮用例确认并发写者被拒绝。

不过替代方案是**在原文件句柄上原位写入**，不是原子替换。源码明确有 Write、SetLength、Flush 多阶段，修订 2 也登记了崩溃后半写风险。不能把它表述为恢复了原有事务级原子性。

共享 CommitTextEditTool 会在调用前持久化原文备份和提交日志，因此这里不认定“没有备份”或“必然不可恢复”。但目前没有真实的半写、磁盘满、进程被终止和断电恢复证据。应补故障注入与人工/自动恢复验证，或对无法保持设计边界的自动提交采取明确限制。该部分继续列为设计差异待验收。

## 4. 上轮十项问题的复核状态

| 条目 | 源码/独立构建 | x86 交付包 | x64 交付包 |
|---|---|---|---|
| R01 主线无限超时 | 已修复、两协议构造成功 | 不适用主线 ZIP | 不适用主线 ZIP；主线旧 ZIP 仍失败 |
| R02 上下文与 async 取消状态 | 原复现通过 | 通过 | 旧 DLL，仍失败 |
| R03 WhenAll | 原复现及新增回归通过 | 通过 | 旧 DLL，仍失败 |
| R04 取消注册生命周期 | 原挂起用例修复；不据此声称完全原生等价 | 不再挂起 | 旧 DLL，仍挂起 |
| R05 丢更新 | 原竞态用例修复；原位写设计差异待验 | 并发写者被拒绝 | 旧 DLL，仍可覆盖 |
| R06 目录链接越界 | 原复现通过；新增只读副作用见 S06 | 链接被拒绝 | 旧 DLL，仍越界 |
| R07 PDF | 页数问题修复；混合字体仍错 | 部分修复 | 仍有旧重复循环 |
| R08 能力声明 | 已有统一开关 | 技能/工具一致 | 技能已改，旧工具仍宣称 OCR |
| R09 审计 | 部分修复，损坏 Host 仍放行 | 最终包负例验收不足 | 同左 |
| R10 迁移脚本 | 命名及 check 修复；snapshot 防误删需补 | 开发工具项 | 开发工具项 |

## 5. 证据与后续顺序

隔离证据目录：`artifacts/diagnostics/xp-review-r2/`。

- `review-summary.json`：源码指纹、两平台检查结果与剩余问题摘要。
- `package-binary-comparison.json`：当前包与上轮保存 DLL 的完整 SHA-256 对照。
- `edges-x86.txt`、`edges-x64.txt`：实际 ZIP DLL 的复现；`source-result-x64.txt` 为当前源码新构建对照。
- `constructor-result.txt`：主线源码已修复、现有正式 ZIP 仍失败的对照。
- `xp-regressions-*.txt`、`http-regression-*.txt`：12 个源码回归的执行记录，包含 HTTP.sys 沙箱差异。
- `audit-empty/`、`audit-bad-host/`：空候选失败和损坏 Host 误通过的证据。
- `fixtures/mixed-font-map.pdf`、`font-oracle.json`、`mainline-font-control.txt`：使用 Type0 CJK 与 Helvetica 的有效混排 PDF，以及两种独立提取控制。

建议顺序：先修复 PDF 解码与审计失败路径，再把主线及两个 XP 包从同一冻结快照构建、测试并重新交付；增加“测试 DLL 与 ZIP DLL 散列一致”的硬门禁，更新包内证据，随后补代理/只读副作用/开发脚本保护与文件恢复验证。真实 XP 双平台验收仍未执行，release_ready=false 应继续保持。
