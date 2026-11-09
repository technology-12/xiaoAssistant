# WorkBuddy Linux 迁移：macOS 与 Windows 开发环境评估

日期：2026-11-09。关联：[迁移方案](MIGRATION_PLAN.md)、[AI 开发执行计划](AI_DEVELOPMENT_PLAN.md)。

## 结论

**macOS 和 Windows 都能承担统信版的全部代码开发；选择熟悉的一边作为主开发环境，并在 UOS/Linux 验证。复用当前 Mac 是设备选择建议，不是对 Windows 开发能力的限制。Windows 旧版回归只约束 Windows 产品交付，不阻挡独立统信分支。** 不需要为了编写 Linux 版代码，先把全部工作转移到 Windows。

如果当前最重要的是修复、维护或发布既有 Windows WPF 版本，Windows 更合适；如果重点是新建 Linux 版本的共享内核和跨平台界面，Mac 与 Windows 都能承担。若只能保留一个主开发环境、又必须频繁维护旧 WPF 产品，则优先 Windows x64，但仍要有 Linux 验证入口。

这个结论来自当前仓库的技术结构和平台约束，不代表 Mac 天生比 Windows 更适合 AI 写代码。AI 开发效率更依赖完整源码、可运行的构建入口、自动回归、清晰的小任务和真实目标环境反馈。

## 2026-11-09 调研机器检查结果（历史记录）

本次只做只读检查，没有安装 SDK、IDE、虚拟机或修改应用构建配置。

| 检查项 | 实测结果 | 含义 |
|---|---|---|
| 系统 | `sw_vers` 返回 macOS 27.0.1，构建号 26A434 | 后续环境记录保存原始输出，不能仅写“Mac” |
| CPU 架构 | `uname -m` 返回 arm64 | 本机运行验证属于 macOS arm64，不是 Linux amd64 |
| .NET 命令 | `command -v dotnet` 未找到 | 当前 shell 尚无已验证可用的 .NET SDK；不等同于证明整机绝对没有安装 |
| 项目状态 | 仍为 net48 / WPF；没有现代 .NET Linux 入口 | 现在不能直接用 `dotnet run` 启动既有桌面程序 |
| 源码快照 | 447/476 文件；全部应用源码和 OCR 资源齐全，缺 29 个测试文件；无 Git 历史 | 执行时核对实际 checkout；完整检出无需重复修复 |
| 目标系统 | 本次未连接 Windows、UOS/麒麟测试机 | 不把这些环境写成已具备或已通过 |

仓库已有 `docs/LINUX_PORT_HANDOFF.md`，其方向也是“Mac 日常开发、Linux 发布验收”。本评估补充当前机器的启动条件；Windows 旧版回归是条件性安排，不是 Linux 迁移前置要求。

## 按工作类型比较

| 工作 | 当前 Mac | Windows x64 | Linux / UOS |
|---|---|---|---|
| C# 业务代码阅读、修改、AI 协作 | 适合 | 适合 | 适合 |
| net10 共享内核、FakeModel 回归 | 配置 SDK 后可做 | 配置 SDK 后可做 | 可做，也是持续集成目标 |
| Avalonia 新界面开发 | 通过本机最小程序验证后可做 | 可做 | 用于验证真实 Linux 呈现 |
| 原有 WPF 程序运行与调试 | 不能原生运行 | 主要环境 | 不能原生运行 |
| DPAPI、Job Object、TxF、WinVerifyTrust | 不能证明 Windows 实现行为 | 仅维护 Windows 版时在这里回归 | 验证替代实现 |
| Linux UDS、Secret Service、cgroup、文件安全 | 可编写，关键行为不能靠本机证明 | 可编写；WSL 可辅助部分验证 | 必须在目标环境验证 |
| Linux OCR 原生库 | 可做接口与样例开发 | 可做接口与样例开发 | 按架构验证 ELF、ABI 和运行行为 |
| deb 安装、桌面集成、中文输入法 | 不能代表 UOS 验收 | 不能代表 UOS 验收 | 必须验证 |
| amd64 发布验收 | 本机 arm64 结果不能代替 | Windows x64 结果也不能代替 Linux | 使用 Linux amd64 环境 |

WPF/.NET Framework 的 Windows 限制见 [Microsoft WPF 文档](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)。现代 .NET 可在 Mac 上使用，SDK 需选择与处理器相符的架构，见 [Microsoft macOS 安装说明](https://learn.microsoft.com/en-us/dotnet/core/install/macos)。

## 当前 Mac 的额外验证项

Microsoft 的 .NET 10 系统矩阵列有 macOS arm64，但这不能单独证明整个 GUI/原生依赖链可用。[.NET 10 系统矩阵](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)。

本次查阅的 Avalonia 平台表列出 macOS 26、15、14，未明确列出当前机器返回的 macOS 27。因此，本机作为主开发机的建议附带一个短期验证条件：锁定候选 SDK/Avalonia 版本，实际完成“控制台启动、窗口启动、中文输入、文件选择、SQLite 加载”冒烟测试。未通过时，将 GUI 调试切换到受支持的 Windows 或 Linux 环境，Mac 仍可用于编辑和部分共享代码开发。[Avalonia 官方平台表](https://docs.avaloniaui.net/docs/supported-platforms)。

Mac 默认文件系统配置不一定区分文件名大小写；Linux 的路径测试需要在实际目标文件系统运行。macOS arm64 上的 Linux arm64 虚拟环境有开发价值，但不能代替 amd64 验收。x64 仿真最多作为辅助排障，不能替代目标架构环境的性能和兼容性结果。

## 推荐的开发与验证组合

| 环境标识 | 推荐用途 | 最低要求 |
|---|---|---|
| DEV-MAC | 日常 AI 开发、共享内核、Avalonia 界面、无 GUI 回归 | 本机 arm64 .NET 10 SDK；最小程序验证通过 |
| WIN-REG（可选） | 仅同时维护 Windows 产品时：原 WPF 行为对照、net48 回归、Windows 发布 | Windows x64、适用 SDK、.NET Framework 4.8、原发布所需依赖 |
| LINUX-X64 | Linux 内核与工具快速回归 | 原生 Linux amd64 运行环境；普通用户执行；可用日志和进程管理 |
| UOS-X64 | 默认首发验收 | 锁定 UOS V25 专业版 amd64 镜像、小版本和图形会话 |
| UOS-ARM64 | 第二批架构适配 | 飞腾/鲲鹏等目标整机；明确 CPU、系统和 GPU/驱动 |
| MATRIX | 麒麟、deepin、openKylin 扩展 | 每个组合独立保存验收结果，不能复用发行版名称代替测试 |

LINUX-X64 与 UOS-X64 初期可合用一套 UOS amd64 环境，减少配置成本。仅做统信版时不需要 Windows 回归机；若维护旧版，可远程使用 Windows。优先复用已有设备和测试资源，不能只为“AI 开发”就推断需要购买新电脑。

Windows 上使用 WSL 可以辅助 Linux 构建和部分运行，但 WSL 的桌面、权限配置及用户服务不等同于 UOS DDE/麒麟 UKUI。Docker 可做依赖受控的无 GUI 回归，不能单独证明托盘、钥匙环、输入法、GPU 或桌面安装通过。[Avalonia Linux/WSL 说明](https://docs.avaloniaui.net/docs/platform-specific-guides/linux)。

## 工具和版本选择

SDK 使用 .NET 10 LTS 的经验证补丁/feature band，落入 `global.json`。Avalonia 和 NuGet 依赖锁具体版本，不让每轮 AI 自动升级到最新版本。IDE 可沿用熟悉的 VS Code；若已有 Rider 或 Visual Studio，也可使用，不把付费 IDE 作为迁移前提。[Avalonia IDE 入口](https://docs.avaloniaui.net/docs/get-started)。

共享代码在所选开发/目标环境使用同一套构建说明、样例与结果格式；Windows 仅在旧产品兼容范围内加入；平台专属代码分目录/程序集。首轮关闭裁剪和 Native AOT。工具版本、测试环境、命令、退出码和产物散列进入交付证据。

Mac 不是本次承诺交付的产品平台；为了在 Mac 上开发，可使用假凭据或仅本次会话凭据，不必顺带开发完整 macOS Keychain、安装包和自动更新功能。若以后要正式发布 Mac 版，再单独明确范围。

## 选择建议

- **按当前统信迁移需求执行：Mac 主开发 + UOS 验收，Windows 不作为前置。** 先完成环境冒烟验证，无需先搬家到 Windows。
- **若旧版 Windows 维护占主要时间：Windows x64 主开发 + Linux/UOS 验收。** Mac 作为可选开发端。
- **任一单一桌面系统都不足以证明多平台迁移完成。** 可以只在一台机器上编辑代码，但仍需访问不同运行环境。

具体执行顺序、任务编号、依赖、交付物和 AI 提示词见 [AI 开发执行计划](AI_DEVELOPMENT_PLAN.md)。
