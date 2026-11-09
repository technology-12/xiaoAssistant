# 晓助手统信 / 国产 Linux 迁移文档

这里归档 2026-11-09 的源码调研、开发环境评估及 AI 执行计划。**统信产品基线目前为规划状态，尚无已验证可运行的 Linux 发布物。**

macOS 和 Windows 都可以承担统信版的全部代码开发，系统集成与发布验收在目标 UOS/麒麟环境完成。旧 Windows 版回归仅约束其自身交付，不是统信开发前置。

## 阅读入口

1. [双基线管理](../../baselines/README.md)：Windows 与 UOS 如何共同维护。
2. [迁移方案](MIGRATION_PLAN.md)：架构、Windows 依赖、平台改造、适配矩阵及估算。
3. [开发环境评估](DEVELOPMENT_ENVIRONMENTS.md)：macOS/Windows/Linux 的分工和选择。
4. [AI 开发执行计划](AI_DEVELOPMENT_PLAN.md)：21 个任务、依赖、验收、证据和接力指令。
5. [机器可读任务清单](tasks.json)：初始任务均为 TODO，不能视为迁移完成证据。
6. [进度记录](PROGRESS.md)：已完成的文档整理与后续待办。

[本地调研下载记录](research/source-snapshot-2026-11-09.json)属于历史证据：447/476 仅描述当时的本地下载，不表示 GitHub 仓库缺少 29 个测试文件。执行开发时以实际 Git checkout 为准，不能据此删除或重建仓库。

## 状态与版本

- Windows 源码版本仍为 1.0.7，不因整理文档而提升程序版本。
- UOS 基线编号 `uos-b0`，状态 `planning`，计划版本 v1.1；不等于 UOS 软件 1.0.7 已发布。
- Windows XP 既有基线继续保留，不在本次文档整理中重新定义其验收状态。
- .NET 10、Avalonia、Linux 构建入口和安装包均属于后续实施内容，本文档提交未实现这些功能。
