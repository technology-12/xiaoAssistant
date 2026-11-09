# Windows 与统信 UOS 双基线管理

本仓库同时管理既有 Windows 产品和规划中的统信 UOS 产品；“同仓库”不代表两个平台已经具备同等实现或验证状态。

| 基线 | 入口 | 现状 | 验证边界 |
|---|---|---|---|
| Windows 主线 | [windows-main](windows-main/README.md) | 1.0.7，net48/WPF 既有源码 | 既有本机验证记录不替代 Win7 等目标实机验收 |
| 统信 UOS | [uos](uos/README.md) | `uos-b0`，迁移规划，尚无 Linux 发布物 | 首发计划 UOS V25 amd64，其后按矩阵分别验收 |
| Windows XP | [windows-xp](windows-xp/README.md) | 原有独立基线保留 | 以 XP 基线文件和对应发布记录为准 |

## 分支和版本规则

建议使用以下分工；分支名称是管理约定，是否实际创建以 GitHub 分支列表和本次上传结果为准。

- 默认分支：保留已有开发历史，不因上传计划文档强制改名或覆盖。
- `baseline/windows-1.0.7`：指向核对过的 Windows 1.0.7 源码提交，作为冻结来源，不在该分支开展 UOS 改造。
- `baseline/uos`：从同一来源建立，先归档本次规划，再实施 Linux 独立构建和平台层。
- 日常改造使用短期功能分支，完成相应平台验证后合入对应产品分支；共享修复通过独立提交选择性同步。

不使用强制推送改写现有分支，不为“看起来有两个版本”复制整套源码目录，不把规划基线标为可发布版本。本次不创建软件发布标签，也不改变 `Directory.Build.props` 中的 1.0.7。

如果远端默认分支已前进，先检查变化并记录实际来源提交。来源 commit SHA 为 `4ef75b18004ec7e902d46cb9d4d69270e5ad2655`，tree SHA 为 `c7cea0e5fe084101c20b18b9a470763c0e44beba`，已通过真实 Git 对象核对。早先调研将提交 SHA 误标为 tree SHA，本次已更正；只有明确的软件发布流程才创建版本标签。

## 代码边界

Windows 继续使用现有 WPF/net48 入口。UOS 后续建立独立现代 .NET/Avalonia 入口；Core、Tools、模型协议等按平台契约共享。DPAPI、Job Object、Win32 文件安全、TxF 等通过平台实现隔离，不直接把全局目标从 net48 改掉。

Linux 实现不要求先在 Windows 编写或运行。Mac、Windows 均可作为开发机；UOS 能力在真实目标环境验证。修改独立 UOS 分支不以 Windows 回归为门禁；影响共享代码且进入 Windows 产品时，Windows 分支才需完成相应回归。

## 合入与发布

每个平台分别保存源码提交、依赖锁、环境、命令、测试数、失败/跳过项、构建散列和包散列。平台状态区分规划、构建通过、运行通过、验收通过。未经该平台实际验证，不将另一个平台的测试结论直接继承为发布资格。

UOS 首版优先 amd64，后续扩展 UOS V20/V25 arm64、银河麒麟 V10/V11、deepin、openKylin；LoongArch 先完成全链路可行性证明。详情见 [迁移文档](../docs/linux-port/README.md)。
