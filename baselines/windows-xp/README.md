# 晓助手 XP B0 产品基线

建立日期：2026-11-08。来源：当前 1.0.5 C# 本地内核。状态：**兼容性迁移待实施，尚无可交付的 XP 软件包**。

本目录建立新的产品约束和迁移入口，不把现有 net48 程序重新命名为 XP 版。现有主线、发布包和用户配置保留原状。

按用户确认，目标同时覆盖 **Windows XP SP3 32 位**和 **Windows XP Professional x64 Edition SP2**，统一采用 .NET Framework 4 Full，分别构建 x86/x64 程序。两套系统分别验收、分别交付，不能仅用 x86 程序在 x64 上启动替代完整验收。内存暂以 2 GB、显示分辨率 1024×768 建立验证配置，需用实测修订，尚非性能承诺。

- [机器可读产品约束](baseline.json)：执行位置、功能范围、联网限制和发布门槛。
- [冻结的 B0 来源指纹](source-lock.json)：记录建立基线时的源文件、项目和解决方案散列；后续审计报告列出相对此来源的变化，不覆盖本文件。
- [完整迁移与验收方案](../../docs/WINDOWS_XP_PRODUCT_BASELINE.md)：给后续开发工具的工作包、依赖关系和交付标准。
- [审计工具](../../scripts/Audit-WindowsXpBaseline.py)：生成带文件位置和源码指纹的审计报告。

在工程根目录运行（Python 仅用于开发检查，XP 用户不需要安装）：

```powershell
python scripts/Audit-WindowsXpBaseline.py
python scripts/Audit-WindowsXpBaseline.py --check
```

报告：`artifacts/diagnostics/windows-xp/audit.md`、`audit.json`。两个目标各有 9 项门槛，共 18 项，必须全部通过。第二条当前应返回 **2**，表示不能发布 XP 版；不是审计工具运行故障。当前审计器只做 B0 阻塞检查，没有真实 XP 证据验证器，即使减少到零处匹配也不会自动放行。

原始功能必须保留：本地 C# 编排、可读写并发子任务及冲突协调、上下文压缩、三档权限、技能与 Skill Creator、Word/PPT/Excel 和 PDF、本地持久化、工作区发现、内联交互、成果链接。迁移过程中的实验程序必须标为实验程序，不能作为完整产品交付。

模型计算可走其他厂商 API 或指定 IP；文件遍历、文档生成、技能执行、权限判断及子任务调度仍在 XP 本机。现代主机上的“模型协议/TLS 网关”可选，其职责不包括远程智能体编排或办公工具执行。

后续开发从方案中的 **XP-01** 开始，在 `src-xp/` 建立独立构建入口；不要全局把 `Directory.Build.props` 的 net48 改成 net40。每个工作包完成后附真实构建/运行证据并更新进度，不能只更新状态字段。
