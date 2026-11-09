# WorkBuddy（晓助手）迁移统信 UOS 与同类国产 Linux 方案

调研日期：2026-11-09。对象：`technology-12/xiaoAssistant`，Windows 主线版本 1.0.7。

## 1. 决策建议

推荐采用 **保留 C# 业务内核、建立现代 .NET 10 构建、重做 Avalonia 桌面壳、补齐 Linux 平台实现** 的原生迁移路线。

按用户确认，先采用默认配置并扩大适配规划：**首发 UOS V25 桌面专业版 amd64，兼容扩展 UOS V20 amd64，随后加入 arm64 与银河麒麟、deepin、openKylin**。完成真实目标机验证后逐批开放支持。龙芯 LoongArch 单独立项做运行时与原生库可行性验证，不纳入首批无条件支持承诺。UOS V20/V25、银河麒麟 V10/V11 不是一个统一的二进制目标，必须分别记录版本、架构、运行库和桌面会话。

现有程序并非只需“重新打包成 deb”：WPF、DPAPI、Windows Job Object、Win32 文件句柄校验、NTFS TxF 事务、Windows 原生依赖与更新器都需要处理。尤其文件安全和任务终止语义，不能为了编译通过而删除。

建议首个可交付版本覆盖聊天、模型配置、工作区、审批、文档读取与生成、任务历史和取消；OCR 随原生库验证进度纳入；原文件事务编辑在 Linux 上另设验收门槛。若第一版尚不能提供所需的并发写保护，则明确禁用该操作，并保留生成新版本文件的替代流程。

## 2. 软件理解与源码证据

用户称 WorkBuddy，仓库及界面名称为“晓助手”，程序集使用 `WorkAssistant.*`。本方案以这个仓库为对象，不套用其他同名软件的架构。

当前架构可以概括为：

```text
WPF Desktop：会话、项目、任务队列、设置、审批、成果展示
    │ 本机 IPC：身份验证、nonce 握手、事件、审批、取消
Task Host / 文档 Worker：独立进程执行与资源约束
    ├─ Core：Agent 循环、上下文、预算、工具分发、子任务
    ├─ Infrastructure：模型协议、HTTP/SSE、状态库、配置、凭据
    └─ Tools：工作区策略、文件安全、CSV/XLSX/DOCX/PPTX/PDF、OCR
```

README 明确：当前主线直接连接模型服务，不需要 Node.js、OpenCode 进程或独立网关；`migration/` 中保留的网关属于历史迁移资料。Linux 版应沿用本机内核路线，除非后续明确决定改变部署架构。

| 位置 | 核查到的事实 | 迁移含义 |
|---|---|---|
| `Directory.Build.props` | 全局目标 `net48`，版本 1.0.7 | 需要独立现代 .NET 构建入口，避免直接破坏旧 Windows/XP 线 |
| `src/WorkAssistant.Desktop/WorkAssistant.Desktop.csproj` | `UseWPF=true`，引用 WinForms、System.Drawing | 现有桌面程序不能直接在 Linux 原生运行 |
| `MainViewModel.cs`、`MainWindow.xaml.cs` | Dispatcher、CommandManager、NotifyIcon、FolderBrowserDialog、`explorer.exe /select` | 视图模型也混有平台调用，不能整体直接复用 |
| `Core/AgentController.cs` | 本地 Agent 编排、工具与成果状态管理 | 可复用的核心资产，优先迁移并建立无界面回归 |
| `Infrastructure/TaskHostClient.cs` | Windows 管道 ACL、客户端 PID 校验、随机 nonce、启动 Host.exe | IPC 必须保留身份和审批绑定语义 |
| `Infrastructure/ProcessJob.cs` | Job Object 约束内存、活动进程数及退出时清理 | Linux 需重新实现进程树生命周期与资源限制 |
| `Infrastructure/SettingsStore.cs` | DPAPI `CurrentUser` 加密 API Key | 凭据存储需平台化；旧密文不能直接搬到 Linux 解密 |
| `Infrastructure/SqliteStateStore.cs` | System.Data.SQLite；WAL、FULL 同步、schema 版本、备份恢复 | 数据库格式可迁移，但驱动和运行库需要替换与回归 |
| `Tools/VerifiedFiles.cs` | kernel32 句柄打开、最终路径、硬链接校验、句柄移动 | 是文件安全边界，需要 Linux 专门实现 |
| `Infrastructure/ProjectConfigurationFiles.cs` | 项目配置目录逐级句柄锁定、链接拒绝、最终路径校验 | 项目规则和技能配置读取也需接入跨平台安全文件层 |
| `Host/IsolatedTool.cs` | 文档 Worker 另有 Windows 管道 ACL/PID 校验 | 不能只改主 Host IPC，遗漏工具 Worker 通道 |
| `Tools/AtomicTextTransaction.cs` | KtmW32、CreateFileTransacted、NTFS 事务；不支持时明确拒绝 | Linux rename 与文件锁不能自动提供同等事务隔离 |
| `Core/DurableFiles.cs` | 同目录临时文件、Flush(true)、File.Replace、备份 | 需验证 Linux 上的提交持久性和恢复语义 |
| `Tools/WordAutomation.cs`、`PresentationAutomation.cs` | OOXML ZIP/XML 处理 | 已检查的生成逻辑不依赖 Office COM，迁移收益较高 |
| `Tools/WorkAssistant.Tools.csproj`、`PdfOcr.cs` | PdfPig、SkiaSharp 2.88.9、Tesseract 5.2.0 | 已有原生依赖，需按系统/架构重新验证 |
| `Infrastructure/NativeRuntimeBootstrap.cs` | Windows DLL 搜索和显式加载 | 不能携带 Windows DLL 作为 Linux 依赖 |
| `Infrastructure/TrustedUpdates.cs` | setup.exe、WinVerifyTrust/发布者校验 | Linux 安装与更新机制需要独立设计 |

README 含历史设计记录，不能把其中“PDF/OCR 不支持”“无 native 依赖”等旧描述当成当前事实。以实际项目引用、`PdfOcr.cs` 和 `docs/RELEASE_1.0.7.md` 为准。

仓库已有 `docs/LINUX_PORT_HANDOFF.md`，建议 .NET 10、Avalonia、XDG、Secret Service、现代 IPC，并强调文件安全。本方案沿用这一方向，新增统信/麒麟目标矩阵、原文件编辑语义边界、发布门禁和工作量估算。

## 3. 统信及同类系统调研

### 3.1 统信桌面版

统信官方资料列有桌面专业版 V20 和 V25；V25 于 2026-04-15 发布。厂商提供不同处理器架构版本，因此“支持统信”必须落到明确镜像和 CPU 上。[统信产品资料](https://www.uniontech.com/Documentation.html)、[V25 官方发布说明](https://www.uniontech.com/news-info/2877.html)。

统信官方安装 FAQ 说明桌面软件使用匹配 CPU 架构的 `.deb` 包；amd64 常见于海光、兆芯、Intel、AMD，arm64 常见于飞腾、鲲鹏等；较新的龙芯使用 LoongArch，旧龙芯还有 MIPS。打成 deb 不会把 Windows EXE 或 x64 二进制转换成其他架构可执行程序。[统信架构与安装包说明](https://faq.uniontech.com/desktopv25/e27f/1261)。

统信官方还提示部分机型涉及 Wayland，实际部署需采集会话类型，不能只按发行版名称判断。[统信系统版本和 CPU 查询说明](https://faq.uniontech.com/desktop/f435/8f32/6612)。

### 3.2 银河麒麟等同类国产桌面

银河麒麟官方目前同时提供 V10、V11 桌面产品和多架构下载。V11 引入开明包，官方说明可转换 deb；其安装和运行边界仍应在真实版本验证，不能假定某个 UOS 包在麒麟必然可用。[官方镜像下载](https://www.kylinos.cn/support/trial/download/)、[银河麒麟 V11 产品说明](https://www.kylinos.cn/productPc/desktop/desktopMainV11/)。

deepin 25 系列、openKylin 2.0 SP2/3.0 列入扩展验证范围。其官方页面均提供多架构信息，但操作系统本身支持某 CPU，不代表晓助手的 .NET、Skia、OCR 依赖链也已支持。社区环境不能替代客户采购的 UOS/银河麒麟版本验收。[deepin 官方下载](https://www.deepin.org/zh/download/)、[openKylin 官方下载](https://www.openkylin.top/downloads/)。桌面系统与服务器系统的 GUI、会话服务、软件仓库也不同，本项目首要目标应为桌面版。

### 3.3 建议支持矩阵

| 优先级 | 目标 | 承诺前提 |
|---|---|---|
| P0 | UOS V25 + amd64 | 默认首发基线；锁定具体镜像、小版本、整机和会话 |
| P1 | UOS V20 + amd64 | 针对旧内核/系统库验证，不因 V25 通过而免测 |
| P1 | UOS V25/V20 + arm64（飞腾/鲲鹏等） | 两个系统版本分别验收；确认 SQLite、Skia、OCR 和驱动 |
| P1 | 银河麒麟 V10/V11 + amd64 | 两个系统版本分别验证系统库、D-Bus、桌面集成与安装包 |
| P2 | 银河麒麟 V10/V11 + arm64 | 前述共性适配稳定后扩展，不能用 amd64 结果代替 |
| P2 | deepin 25 系列 + amd64/arm64 | 验证 DDE 集成和发布方式；玲珑分发另做专项 |
| P2 | openKylin 2.0 SP2 + amd64/arm64 | 验证 UKUI、钥匙环、托盘及图形会话 |
| P3 | openKylin 3.0 + amd64，arm64 按具体整机镜像评估 | 新环境跟进，避免把开发板镜像当作通用整机支持 |
| 独立预研 | UOS/麒麟/社区版 + LoongArch | .NET 运行时、Skia、SQLite、OCR 全链路证明后再纳入 |
| 暂不承诺 | MIPS、SW64、RISC-V | 保留平台接口扩展点，有明确业务设备后另评估 |

上表全部为**计划适配**，并非已经通过。为扩大覆盖面，采用“同一业务核心 + 两个主要 RID + 各发行版平台配置”的方式；能力检测记录 `secure_file_open`、`original_edit`、`worker_limits`、`secret_store`、`ocr`、`tray` 等支持状态。核心安全能力不满足时拒绝相应操作，可选桌面能力缺失时给出明确提示。

首发前采集：`/etc/os-release`、`uname -m`、`uname -r`、`getconf GNU_LIBC_VERSION`、`XDG_SESSION_TYPE`、`systemd --version`、可用 cgroup 控制器、Secret Service 与 portal 后端、GPU/驱动、离线和代理要求。只采集必要环境信息，不收集模型密钥。

## 4. 技术路线比较

| 路线 | 可复用资产 | 主要代价 | 建议 |
|---|---|---|---|
| .NET 10 + Avalonia 原生桌面 | C# 内核、协议、工具、部分 MVVM | 重做视图、平台边界、原生依赖 | 主路线，优先 amd64/arm64 |
| Wine/deepin-Wine 运行现有程序 | Windows 产物 | WPF、TxF、管道、Job Object、OCR 均需实测；跨 CPU 更复杂 | 只做有时间上限的对照 PoC |
| Web 前端 + 本机 .NET 服务 | C# 后端 | 重写前端；额外本机服务鉴权、浏览器边界与部署 | 有明确 Web 产品需求时考虑 |
| Qt/C++ 等全面重写 | 产品逻辑和文档规范 | Agent、状态、工具和大量测试需要重做 | 多种非主流 CPU 为硬要求时再评估 |

Microsoft 明确 WPF 与 .NET Framework 的 Windows 属性；仅把目标框架改成现代 .NET 不会让 WPF 跨平台。[WPF 官方说明](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview)。

统信确有 Windows 应用兼容引擎及商业迁移服务，但这只能证明平台提供兼容路线，不能证明晓助手当前全部安全语义受支持。[统信 Windows 应用兼容引擎](https://www.uniontech.com/news-info/2489.html)。

## 5. 推荐架构和具体改造

### 5.1 构建与共享内核

新增 `Xiao.Linux.sln` 和 `WorkAssistant.Desktop.Avalonia`；Linux 构建目标使用 `net10.0`。共享业务项目按实际 API 兼容度选择多目标 `net48;net10.0`，或独立跨平台项目复用同一批源码；全局 `Directory.Build.props` 中框架、Windows 引用需加条件。XP 线独立保留，避免被跨平台构建牵连。

拟新增接口（为方案建议，当前尚未实现）：

```text
IAppPaths              配置、状态、缓存、运行期目录
ICredentialStore       DPAPI / Secret Service
IHostTransport         Windows 管道 / Linux UDS
IProcessSupervisor     Job Object / Linux 进程与资源管理
IVerifiedFileSystem    安全打开、身份校验、受控移动
ITextCommitter         各平台支持的文本提交能力
IDesktopIntegration    选目录、打开成果、托盘、通知
IUpdateService         各平台可信更新
```

先迁移 Agent 和文档核心，使用 FakeModel 建立无 GUI 入口；随后接进程隔离和存储；最后迁移桌面。这样能把业务问题与 UI/系统问题分开定位。

开发可继续使用 Mac，但发布验证建立独立 Linux amd64 与 arm64 运行环境；交叉发布成功不算目标架构验收。CI 分开保存两种 RID 的构建、原生库探测、无 GUI 回归和包安装结果，真实整机补充 GPU、输入法、托盘与权限验证。

截至本次调研，.NET 10 是 LTS；.NET 8 的支持期已接近结束，不建议新迁移线以其为长期基线。官方 .NET 10 支持矩阵未直接列 UOS、麒麟、LoongArch；“可以运行”和“官方支持”要分别记录。官方便携 Linux x64/arm64 运行时列出的 glibc 基线为 2.27，但仍须核查实际补丁版及 OpenSSL、ICU、libstdc++ 等完整要求。[.NET 生命周期](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)、[.NET 10 系统矩阵](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。

### 5.2 桌面界面

保留项目/会话导航、流式消息、工具步骤、成果卡片、任务队列、审批与问题卡等产品设计，迁移到 Avalonia。`System.Windows`、Dispatcher、CommandManager、WPF 资源与控件模板需要逐项替换，不能直接复制 XAML 后宣称完成。

文件夹选择、托盘、剪贴板、通知、默认应用打开和文件管理器定位统一走平台接口。Linux 打开文件使用受控 URI/参数列表，避免把文件名拼接成 shell 命令。

Avalonia 当前官方文档说明 Linux 默认 X11，在 Wayland 可经 XWayland 运行，12.1.0 起另有可选原生 Wayland 后端。选定具体版本后锁定包与依赖；UOS 不在其常规已测试发行版名单内，需项目自己验收。不能把 Skia 的 glibc 2.17 基线等同于整个 .NET 应用的最低系统要求。[Avalonia 平台矩阵](https://docs.avaloniaui.net/docs/supported-platforms)、[Linux 后端说明](https://docs.avaloniaui.net/docs/platform-specific-guides/linux)。

中文字体采用有分发许可的字体方案或目标系统字体回退；当前 PPT 生成中硬编码“微软雅黑”“Calibri”，应改成可配置字体，并用目标 WPS/LibreOffice 检查换行、表格、页码、图标和缺字。OOXML 包结构校验通过不等于办公软件呈现一致。

### 5.3 工作区文件安全：最高优先级

不能只把 `\\` 换成 `/`。需要同时处理：大小写区分、路径分段、符号链接、硬链接、目录重命名、并发替换、挂载点、只读权限和异常中断。路径比较应封装为平台策略，不全局替换业务字段、模型名或摘要值的大小写比较规则。

Linux 安全打开优先采用工作区根目录 fd 与 `openat2` 的 `RESOLVE_BENEATH`、`RESOLVE_NO_SYMLINKS`，并对已打开文件做 `fstat` 类型、链接数、身份验证；不支持该系统调用时，选择经过专项验证的逐级 `openat` 实现或拒绝相关操作，不能退回字符串前缀判断。`openat2` 自 Linux 5.6 引入，旧 UOS 内核或厂商回移植情况需要运行时探测。[Linux man-pages：openat2](https://man7.org/linux/man-pages/man2/openat2.2.html)。

`openat2` 解决安全解析，不等于解决全部写入竞争。移动和提交必须单独设计：目标父目录 fd、禁止覆盖、源文件身份/摘要、批准指纹、执行前复核、操作后校验和中断记录。Linux 的目录 fd 也不会像 Windows 的独占目录句柄那样阻止其他进程重命名目录，须将这种差异纳入威胁模型。

**原文件编辑必须明确语义：** 当前 TxF 路径要求与不配合的写入者隔离；Linux 的 `flock` 通常为协作锁，摘要复核后再 rename 仍存在竞争窗口，不能声称等价。

首版建议：继续提供提案、审批、备份与新文件成果；原路径覆盖功能在没有证明满足其承诺前返回明确“不支持”，不做静默降级。若业务必须覆盖原文件，则另行评审受管理目录/单写入者契约、排他控制能力及用户可见说明；无法实现原 Windows 契约时，需要明确变更产品承诺。

对应用自己管理的成果和状态文件，可采用同目录临时文件、校验、文件 fsync、原子 rename、父目录 fsync、意图日志与恢复记录。原子可见性、崩溃持久性和防止并发覆盖是三个不同目标；不要用一个 rename 统称全部解决。跨文件系统、SMB/NFS、U 盘文件系统另设能力限制与测试。

### 5.4 Host、Worker 与 IPC

Linux 优先 Unix domain socket，放入每用户运行目录下的私有子目录（目录 0700，socket 限当前用户）。校验对端 UID/PID、保留 nonce 握手、任务/请求 ID、审批指纹绑定、超时、取消与帧长度限制；明确同 UID 恶意进程并非完全由文件权限隔离。

进程管理优先验证 systemd 用户服务/临时单元与 cgroup 资源限制，保留整棵任务进程树退出、内存上限、主进程死亡处理、OOM/崩溃恢复等行为。资源控制依赖系统配置和用户委派权限，不能假定每台 UOS 都开启所需控制器。旧系统只用进程组清理不能自动替代 Job Object 全部功能；若达不到既定边界，应拒绝相关执行模式或缩小受支持矩阵。

注意：systemd `TasksMax`/cgroup pids 计数包含线程，不能照搬 Windows 的“最多 2/3 个进程”为 2/3，否则 .NET 线程池及 OCR 很容易无法启动。进程数政策与线程/任务数预算分别配置。[cgroup v2 官方文档](https://www.kernel.org/doc/html/latest/admin-guide/cgroup-v2.html)、[systemd 资源控制](https://www.freedesktop.org/software/systemd/man/latest/systemd.resource-control.html)。

### 5.5 凭据、目录与历史数据

配置、数据、状态、缓存和 socket 遵循 XDG：`~/.config/xiaoassistant`、`~/.local/share/xiaoassistant`、`~/.local/state/xiaoassistant`、`~/.cache/xiaoassistant` 和 `$XDG_RUNTIME_DIR` 下的私有目录。安装目录只放程序；用户数据不写 `/opt` 或 `/usr`。[XDG 规范](https://specifications.freedesktop.org/basedir/latest/)。

Linux 密钥使用目标桌面实际支持的 Secret Service，并验证锁屏/解锁、无服务和无图形会话情况。不可用时可以提供仅本次进程使用的密钥，或要求显式配置其他安全存储方式，不自动明文落盘。[Secret Service 规范](https://specifications.freedesktop.org/secret-service/latest/)。

Windows 历史迁移按“旧应用导出—新应用导入”设计：先备份，保存 schema 版本、项目、会话、成果索引，重新绑定盘符路径，未完成任务转为中断/待复核。API Key 默认要求重新输入，避免导出明文或复制 DPAPI 密文后误判可用。

SQLite 建议切换 `Microsoft.Data.Sqlite` 与明确的 SQLitePCLRaw/native provider，保留 WAL、事务、busy timeout、同步级别、schema、备份和恢复约束。旧 WAL 数据库必须通过数据库备份 API 或受控 checkpoint 导出，不能仅在程序运行时复制主 db 文件。替换 provider 后单独回归备份 API、并发和连接池行为。[Microsoft.Data.Sqlite 原生 provider 说明](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)。

### 5.6 文档、OCR 与模型连接

CSV、XLSX、DOCX、PPTX 的托管处理优先复用；保留数值精度、公式注入防护、生成后重读校验、限制和成果证据。文本 PDF 先通过，再接扫描件 OCR。

OCR 需验证 PdfPig 渲染、Linux SkiaSharp/HarfBuzz（若依赖链使用）、Tesseract/Leptonica 的 ELF 架构、ABI 和语言数据。现有 Windows DLL、VC++/UCRT 文件不能复用。优先选能稳定支持 Linux 的绑定及受控本地库部署；如果换为 Tesseract 子进程，应保留 Worker 隔离、限时、限内存、取消、路径控制及不调用任意 shell 的约束。

OCR 语言数据可在验证散列和许可证后复用，识别引擎与 traineddata 是两个独立交付部分。[Tesseract 官方安装说明](https://tesseract-ocr.github.io/tessdoc/Installation.html)。

保留 HTTP/SSE、重试、取消、供应商协议和子任务预算；重新测试系统 CA、企业代理、自签企业根证书、离线模式及真实网关。取消 Windows 特有 TLS 假设，不禁用证书验证作为兼容办法。

### 5.7 打包、签名和更新

先交付按 RID 区分的自包含目录/`.tar.gz` 用于测试，再制作 UOS `.deb`。自包含包含 .NET 运行时，但仍有 glibc、图形库、字体等系统依赖。首轮关闭裁剪和 Native AOT，先验证现有反射、JSON 序列化及原生库加载；待完整回归后再评估体积优化。

amd64/arm64 使用独立发布物和原生库目录，安装到应用目录，添加 `.desktop`、图标、卸载和版本信息。构建锁依赖、保存 SBOM、许可证、散列及验收报告；不运行要求用户手工关闭安全机制的安装流程。

Linux 首发建议通过签名仓库或受控离线安装包升级，保留配置和工作区。自动更新另行实现清单验签、架构匹配、下载摘要和回退；不能直接沿用 setup.exe、Authenticode/WinVerifyTrust。

## 6. 实施计划与工期

以下为工程估算，不是已完成测试结论。假设一名熟悉现有代码的 C# 开发者主力投入，能及时获得目标 UOS 整机与测试支持；首批 amd64，保留主要办公闭环，不要求首版等价 TxF 原文件编辑。

| 阶段 | 内容 | 验收出口 | 估算 |
|---|---|---|---|
| 0 | 冻结源码、确认镜像/CPU/会话、依赖和威胁模型 | 兼容矩阵、PoC 门禁、功能边界 | 2–4 人日 |
| 1 | net10 构建、无 GUI Agent/工具闭环、解耦平台接口 | FakeModel 的读取—审批—生成—取消闭环 | 5–8 人日 |
| 2 | Linux 文件安全、IPC、进程管理、凭据、状态库 | 越界/竞争/中断/OOM/导入恢复验证 | 10–15 人日 |
| 3 | Avalonia UI、队列、审批、项目/设置、成果打开 | 主要 Windows 用户流程逐项对照 | 10–15 人日 |
| 4 | OCR、中文字体、桌面集成、deb 与干净机测试 | 安装升级卸载、文档呈现、真实模型冒烟 | 8–12 人日 |

合计约 **35–54 人日**；单人约 **7–11 周**，加上目标机取得、缺陷修复和试点缓冲，项目排期建议 **8–12 周**。这不是把阶段时间简单压缩就能承诺的交付日期。

arm64 在主线完成后追加约 5–10 人日；若出现图形/OCR ABI 问题需重估。第二发行版追加约 3–7 人日验证与打包，不保证没有额外改造。LoongArch 先用 3–5 人日输出可行性结论，不据此承诺最终适配工期。需要保留现有原文件事务编辑全部语义时，单独估算，不包含在上述基线。

按用户希望的更广适配，建议分两次发布：第一批完成 UOS V25/V20 amd64 与优先 arm64；第二批完成银河麒麟 V10/V11，并加入 deepin/openKylin。全矩阵在首版基线上需要额外约 15–30 人日适配与回归（已包含上述 arm64 和第二发行版的增量，不重复相加），单人总体排期可先预留 **12–18 周**，取得各系统和硬件样机后再滚动修订。新增系统/架构的工作有共性，但不能按“换一个安装包后缀”估算。

## 7. 验收门禁

1. **业务闭环**：多轮聊天、两类模型协议、SSE 中断/续接、模型错误、子任务、审批拒绝、取消、成果证据与任务恢复；用 FakeModel 做确定性回归，真实模型只做明确范围冒烟。
2. **文件边界**：大小写不同的路径、中文/空格/特殊字符、相似路径前缀、`..`、符号/硬链接、父目录替换、目标已存在、并发改写、磁盘满、只读目录和跨卷；任何失败不得越界或误覆盖。
3. **进程边界**：错误对端、失效 nonce、Host/Worker 崩溃、UI 被强杀、超时、取消与 OOM；确认没有遗留 Worker 持续写入，写入不确定时进入待复核。
4. **文档**：标准样例 CSV/XLSX/DOCX/PPTX、中文文本 PDF、混合页和扫描件；目标 WPS/LibreOffice 检查显示，OCR 校核语言、低置信度提示、缓存和取消。
5. **状态和凭据**：干净用户、锁定钥匙环、旧库导入、WAL 备份、崩溃恢复、路径重绑定；日志和导出不含密钥。
6. **系统和发布**：每个承诺支持的 OS×CPU×会话组合，在普通用户、干净安装、离线/代理条件下完成安装、启动、升级、卸载与数据保留；100%/125%/150% 缩放、多屏和中文输入法。

`docs/RELEASE_1.0.7.md` 记录的是既有 Windows 环境的验证结果，不是本次复测，也不能算 Linux 验收。该记录中的主线控制台 93/93、UI 44/44 可用作回归范围参考；依赖 WPF、Windows 文件系统或旧测试宿主的用例需要重写适配，不能直接声称复用后已通过。

## 8. 需要项目方最终明确的输入

- 实际客户系统：UOS V20/V25 的具体小版本，或银河麒麟的具体版本。
- CPU/整机：海光/兆芯 amd64，还是飞腾/鲲鹏 arm64，是否把龙芯列为首批硬要求。
- 产品等价范围：首版是否必须 OCR、原文件覆盖编辑、自动更新、便携模式及完全离线。
- 凭据与网络：外部模型可否访问、企业网关/代理与证书策略。
- 是否需要厂商应用商店、适配认证或客户安全审核；这些属于额外交付流程，不等于功能测试通过。

上述输入未明确前，可按“UOS 桌面 amd64 优先、现代 .NET + Avalonia、本机 Agent、生成新成果优先”的假设启动 PoC，但不能承诺覆盖全部国产 Linux 与 CPU。

## 9. 本次核查范围

本次完成架构与关键平台代码静态分析、官方资料调研和迁移设计；没有修改应用实现，没有运行 Windows 程序，也没有 UOS/麒麟实机测试。报告中的建议类接口、工作量、风险分级和技术选型为工程判断，需通过阶段 0/1 的目标机 PoC 验证。

源码来源 commit SHA：`4ef75b18004ec7e902d46cb9d4d69270e5ad2655`；对应 tree SHA：`c7cea0e5fe084101c20b18b9a470763c0e44beba`。早先调研将来源提交误标为 tree SHA，本次通过真实 Git 对象核对后更正。下载完整性记录另见research/source-snapshot-2026-11-09.json。

调研时本地下载结果（非远端完整性结论）：仓库索引共 476 个文件，已恢复并逐文件核对 447 个，包含全部 `src/` 应用源码 166 个文件、全部 `src-xp/` 41 个文件、全部文档、脚本、配置、技能及 OCR 资源。归档传输在末尾中断，尚缺 29 个测试文件，清单见 `WorkBuddy-下载核查.json`；GitHub API 的未登录访问额度随后耗尽。调研时的本地目录是可核查的源码快照，不是成功完成的 Git clone，也不含 Git 历史。仓库索引本身没有 `.exe`/`.msi` 发布物。

这不影响本报告基于完整应用源码给出迁移设计，但不能把该快照写成已完整下载全部仓库文件，也不能据此声称执行过完整测试。4 个 `.cmd` 文件在归档中依照 `.gitattributes` 转换了 CRLF，核查时已还原为 Git 对象中的 LF 后匹配散列。

## 10. 开发环境与 AI 执行计划补充

建议继续用当前 Mac 做共享代码和新桌面壳的日常开发，UOS/Linux 承担目标平台验证。统信迁移不要求 Windows 环境；仅同时维护 Windows 产品时，可选 Windows x64 做原 WPF/net48 回归，不将其设为 Linux 开发前置。当前 Mac 为 arm64，命令行尚未找到 dotnet，需先配置并验证 SDK；本机 GUI 兼容也应先做最小程序验证。完整比较见 [开发环境评估](DEVELOPMENT_ENVIRONMENTS.md)。

迁移工作已拆成 21 个带依赖、环境和验收要求的任务；含源码补齐、构建、平台契约、文件安全、IPC、进程、UI、文档、OCR、发布与多系统适配。执行文档见 [AI 开发执行计划](AI_DEVELOPMENT_PLAN.md)，机器可读状态见 [任务清单](tasks.json)。任务均为计划状态，尚未开始实现。
