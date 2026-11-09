# Windows 主线基线

基线编号：`windows-1.0.7`。当前产品源代码为 C# / .NET Framework 4.8 / WPF，本机 Agent 与文档工具运行在 Windows。

- [机器可读基线](baseline.json)
- [1.0.7 发布与验证记录](../../docs/RELEASE_1.0.7.md)
- 构建入口：`Win7WorkAssistant.sln`、`Xiao.Native.sln`、`src/WorkAssistant.Desktop`。
- XP 产品使用既有 [独立基线](../windows-xp/README.md)，不以本目录替代。

这次文档归档不修改 Windows 应用源码、版本号、发布产物或历史验证结论。Win7/XP 等未完成的实机验证仍按既有记录保留，不能因为建立分支就声称通过。

统信基线从 Windows 产品能力出发迁移，但使用独立 Linux 平台实现。Windows 版可继续独立维护；影响其采用的共享代码时，再做对应回归。
