# 统信 UOS B0 规划基线

基线编号：`uos-b0`。状态：**planning，尚无已验收 Linux 软件或安装包**。来源：晓助手 Windows 1.0.7 的产品能力与本机 C# 内核。

- [机器可读基线](baseline.json)
- [迁移文档索引](../../docs/linux-port/README.md)
- [AI 执行计划](../../docs/linux-port/AI_DEVELOPMENT_PLAN.md)
- [任务状态](../../docs/linux-port/tasks.json)

目标路线为 .NET 10 + Avalonia + Linux 平台实现。Windows 的 UI、凭据、IPC、进程边界、文件安全、OCR 原生库及更新器需要逐项适配；当前仍保留原 Windows 源码，不能直接把分支中的 EXE 当作统信程序运行。

默认目标：UOS V25 桌面专业版 amd64。扩展：UOS V20、arm64、银河麒麟 V10/V11、deepin/openKylin。LoongArch 独立预研。每个系统、架构和图形会话分别验证。

macOS 与 Windows 均可以承担全部 Linux 代码开发；运行、系统集成和最终验收在目标系统进行。Windows 旧版回归不是 UOS 开发前置条件。

下一步从实际 checkout 完整性、环境探针和平台契约开始，依照 T00–T03 推进。原文件覆盖不能满足已定义安全语义时，明确拒绝而不是静默降级。
