# 晓助手 1.0.7 修复与交付说明

日期：2026-11-08。构建：xiao-1.0.7-local-runtime-r1。

本版完成 VC++ 本地部署以及第三轮审查 T01—T03 的代码和打包修复，清除了此前共享控制台及 UI 回归失败。旧 1.0.6 ZIP 保留供追溯，请使用新的 1.0.7 包。

## 修复内容

- 主线 VC++ 2019 14.29.30157.0、UCRT 10.0.10240.16384 以 x86/x64 DLL 随包部署。桌面与 Host 启动时校验清单、设置本进程本机库目录并从绝对路径加载。准备脚本不再安装 VC++，不复制 DLL 到系统目录，不注册系统运行库。97 个随包运行库文件的来源和散列见 native-runtime.json；微软许可随包保留。
- .NET Framework 仍是系统前置依赖：主线 4.8，XP 4 Full。只在缺少时调用随包的微软签名安装程序；检查模式不安装任何组件。
- T01：XP PDF 根据最新 startxref 及 Prev 链选择有效对象，支持普通交叉引用表、xref 流、PNG 预测器、对象流、对象代数和释放条目；页面、字体和正文使用同一有效对象集合，不读取已被更新或未被索引的对象。9 个新增样例验证了内容更新、元数据更新、代数复用、释放对象、孤立旧对象和压缩对象。解压流有大小、取消和超时检查。该实现不是全量 PDF 标准实现，其他过滤器、加密 PDF 等仍会明确拒绝。
- T02：原生 DLL 与 EXE 均检查平台位数，托管 AnyCPU 不受错误限制。5 个自动化发布门禁覆盖合法候选、损坏 Host、双向 SQLite 位数错配及空候选。
- T03：测试程序输出逐用例 JSON、实际计数、build id 和被测二进制散列。新打包器校验计数、通过状态和二进制一致性后放行，包内证据和首次使用说明由同一结果生成。禁止继续调用旧打包器和从历史备份覆盖 XP PDF 的旧生成器，原脚本已归档。
- 修复演示模式跨用户轮次误取上一轮工具结果、空 file_details 字段转换异常；多轮筛选、Excel 导出、归档与对话内审批均纳入界面自检。
- 损坏 XLSX 统一返回格式错误；XP ZIP 构造失败时释放文件句柄，避免后续读取被锁。
- 文件修改前计划过期时保留 PLAN_INVALID，不再被最终 NO_ARTIFACTS 遮蔽。XP 工具描述明确说明独占原位写入，不再向模型宣称具备 NTFS 事务原子性。
- XP 补充 4 个 IO 检查点故障/进程终止回归：备份和提交意图保留，未确认提交不能自动重放。真实断电和实际磁盘满不在这些模拟测试的证明范围内。
- 便携 XP 的用户数据定位修正到软件根目录；工作区成果交付仍为 output，内部任务隔离文件位于 .claude。测试改为核验真实成果路径、版本保留和当前错误分类，未删除安全边界断言。
- 修正测试用 PDF 的流长度、zlib 校验、字体字典及样例文件；本机 HTTP 测试改为 OS 分配端口并处理竞争，消除多平台并发测试的端口碰撞。

- 解压后复测发现并修复旧 Windows 长路径边界：缩短持久化临时文件名，保留完整随机标识与同卷替换；245 字符目标路径的更新/备份回归三线均通过。自检临时状态移到独立短目录，避免安装目录嵌套影响验收。

## 本机验证

以下计数由同一构建的机器可读结果生成，没有沿用旧版的手写计数。

| 产品线 | 控制台 | UI 自检 |
|---|---:|---:|
| 主线 net48 | 93/93 | 44/44 |
| XP x86 | 120/120 | 44/44 |
| XP x64 | 120/120 | 44/44 |

VC++/OCR x86/x64 合计 70/70，PE 发布门禁 5/5；上述合计 540 项检查，失败 0。OCR 包含十页扫描件、任务缓存、取消、Host/Worker 与模块真实加载目录检查，未安装 VC++，未调用真实模型 API。

环境：Windows 10.0.26200 / CLR 4.8。产品源码构建 0 错误、0 警告；测试诊断编译的可空警告不计为产品构建错误。完整证据在 artifacts/repair-1.0.7/verification。真实 XP/Win7 验收范围见下节。

## 发布范围和待验收事项

- 保持三个独立包：主线便携版、XP SP3 x86 试用候选、XP Professional x64 SP2 试用候选。根目录均只保留晓助手.exe、检查运行环境.cmd、准备运行环境.cmd、首次使用说明.txt。
- XP 不含本地 OCR，不能套用主线 OCR 测试结果。
- 当前没有可供本轮使用的真实 XP 环境；XP 双平台的实机启动、旧系统 TLS/代理和旧硬件性能验收仍待完成。Windows 7 实机验收也仍待完成。
- XP 原位写入不能提供事务级原子替换。已验证本机 IO 异常与进程终止的备份/拒绝重放路径；真实断电、磁盘满及 XP 文件系统行为仍需专项实机验收。因此 release_ready 保持 false，不能把本机回归全绿写成已完成真实 XP 支持。

## 重建入口

1. 使用 .NET SDK 按主线 net48 和 src-xp net40 两个平台顺序构建；XP 的 x86/x64 构建共享 obj，不能同时构建同一 checkout。
2. 运行 Prepare-AppLocalVc.py，从固定散列的微软签名包提取 DLL，并复制 SDK Redist 中的 UCRT。构建机需要 7-Zip 和 Windows SDK；用户电脑无需这些工具。
3. 运行 Generate-XpPdfRegressionFixtures.py 生成合规 PDF 样例；运行 Run-ReleaseVerification.py 的 console/UI、Run-AppLocalOcrVerification.py 和 Verify-XpReleaseGates.py。HTTP.sys 测试应在允许本机监听的环境执行。
4. 运行 Build-PortableRelease_1_0_7.py。脚本拒绝覆盖已有包；任何计数错误、回归失败或被测产品散列改变均拒绝打包。

源依据：[Microsoft C++ 本地部署](https://learn.microsoft.com/en-us/cpp/windows/choosing-a-deployment-method?view=msvc-170)、[UCRT 部署](https://learn.microsoft.com/en-us/cpp/windows/universal-crt-deployment?view=msvc-160)、[PDF 交叉引用与增量更新规范勘误](https://pdf-issues.pdfa.org/32000-2-2020/clause07.html)。

## 发行包指纹

| 文件 | SHA-256 |
|---|---|
| XiaoAssistant-1.0.7-portable.zip | `dc743836cf78bfd2b415e7a924382f4e6b9f00557dacc9de041b85db711c4158` |
| XiaoAssistant-1.0.7-portable-windows-xp-x86.zip | `ab94d20d0655e2d26b292bcc33d1929489a4bcd489fdf92d3713e490add478d2` |
| XiaoAssistant-1.0.7-portable-windows-xp-x64.zip | `de7f37442dbb882adc1a01d472e496bc1ae59e55efee2372379ad8aa0d6bd9b8` |

## ZIP 解压后验收

三个 ZIP 均在全新、较深的解压目录复测通过：冻结清单逐文件一致，环境检查 exit 0，主线双协议构造成功，XP 增量混排 PDF 正常；三个根目录启动器的 UI 自检各 44/44。根目录四文件约束满足，发行 ZIP 不含测试 EXE、VC++ 安装器或已有聊天记录。见 artifacts/repair-1.0.7/verification/zip-acceptance.json。该阶段新增 132 项 UI 检查，未替代真实目标系统验收。
