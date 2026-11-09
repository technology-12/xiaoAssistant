# 从 Mac 开发晓助手 Linux 版

本仓库以晓助手 1.0.7 Windows 源码为基线。Mac 用于日常开发，Linux 桌面用于发布验收。建议首批验证 Apple Silicon Mac 和 Ubuntu 24.04 LTS x64，后续按需要加入 Linux arm64。新分支采用 .NET 10 LTS。桌面壳可选 Avalonia，依赖版本和目标系统按官方矩阵锁定。参考 [微软 .NET 支持策略](https://dotnet.microsoft.com/en-us/platform/support/policy) 和 [Avalonia 平台支持](https://docs.avaloniaui.net/docs/supported-platforms)。

```sh
git clone https://github.com/technology-12/xiaoAssistant.git
cd xiaoAssistant
git switch -c linux-port
dotnet --info
```

当前解决方案中的桌面入口是 WPF，Windows 主线目标为 .NET Framework 4.8，XP 线为 .NET Framework 4.0。Linux 分支应先建立独立的现代 .NET 构建入口，便于在 Mac 上验证共享代码，再启动桌面壳开发。

| 现有位置 | Linux 移植工作 |
|---|---|
| `src/WorkAssistant.Core` | 保留 Agent 编排、审批、上下文、工具接口和模型协议。先建立 `net10.0` 构建和无 GUI 回归入口。 |
| `src/WorkAssistant.Tools` | 复用办公文档、任务成果和工作区规则。为 `VerifiedFiles`、`AtomicTextTransaction` 和 `DurableFiles` 明确平台契约，分别验证目录链接、越界、并发修改、同卷提交、备份和中断恢复。Linux 路径按大小写区分，安全校验需要抵御校验到打开之间的路径替换。 |
| `src/WorkAssistant.Infrastructure` | 为本机状态库选择支持目标 RID 的 SQLite 驱动，制定旧库导入和备份方案。配置、缓存和数据分别遵循 XDG 路径。把 Windows DPAPI 封在平台实现中，Linux 使用 Secret Service，开发环境通过本地安全配置注入测试凭据。 |
| `src/WorkAssistant.Host` | 保留进程隔离、握手、取消和审批绑定语义。用现代 .NET 的本机 IPC，评估 Unix domain socket 或经过权限验证的命名管道。为 Windows Job Object 的进程树清理行为提供 Linux 实现。 |
| `src/WorkAssistant.Desktop` | 建立 Avalonia 壳，迁移可复用的视图模型与命令，替换 Dispatcher、文件选择、托盘和打开成果等平台调用。 |
| `src/WorkAssistant.Tools/PdfOcr.cs` | 先验收文本 PDF，再验证 Linux 和 Mac 对应的 OCR 原生库、语言数据、缓存和取消。按 RID 提供对应库。 |
| `src-xp`、`installer` | 作为 Windows 兼容和发布历史参考。Linux 发布入口独立维护。 |

第一阶段在 Mac 上用假模型跑通无 GUI 的完整闭环。用 `samples/workspace` 验证读取、CSV 汇总、Word/PPT 交付、审批和取消。第二阶段在 Linux 测试工作进程、状态库、文件安全、OCR 和断开后恢复，再接入桌面 UI。第三阶段在干净 Linux VM 验收安装与卸载、中文字体、权限受限的工作区、无图形环境下的命令行回归，以及 X11 和目标 Wayland 环境。发布物先考虑 `linux-x64` 自包含目录和 `.tar.gz`，安装格式按目标发行版再定。

本仓库保留技术方案、源码、测试、样例、OCR 模型和第三方声明。默认配置的 API 密钥为空，使用真实模型时在应用内配置。Windows 打包脚本引用本机的验证候选、微软安装器和原生依赖，需要在 Windows 构建机按发布记录准备。OpenCode 网关源码、锁文件与协议保留在 `migration/`，上游检出位置和固定提交见 `migration/UPSTREAM.lock.json`。

技术入口：[1.0.7 验证记录](RELEASE_1.0.7.md) · [正式版实施方案](PRODUCTION_IMPLEMENTATION_PLAN.md) · [内核架构](AGENT_ARCHITECTURE_RESEARCH_AND_IMPLEMENTATION.md) · [权限与上下文](CONTEXT_PERMISSIONS_1.0.5.md) · [文档工作流](DOCUMENT_WORKFLOW_1.0.4.md) · [第三方组件](THIRD_PARTY_NOTICES.md)。
