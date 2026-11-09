# 软件代码行数统计

口径：生产源码仅统计 src 中手写的 .cs 与 .xaml。物理行数含空行和注释；非空行数仍含注释，不等同于逻辑语句数。
排除 bin、obj、生成代码、依赖、构建制品、旧版本副本、样例数据、技能 Markdown、文档及项目配置。测试与构建脚本单独列出；统计没有运行任何测试。

| 类别 | 项目 | 文件数 | 物理行数 | 非空行数 |
|---|---|---:|---:|---:|
| build | installer | 1 | 80 | 71 |
| build | scripts | 9 | 790 | 734 |
| production | WorkAssistant.Core | 29 | 3240 | 3042 |
| production | WorkAssistant.Desktop | 29 | 4768 | 4524 |
| production | WorkAssistant.Host | 9 | 1031 | 1016 |
| production | WorkAssistant.Infrastructure | 29 | 3347 | 3198 |
| production | WorkAssistant.Launcher | 1 | 40 | 39 |
| production | WorkAssistant.OpenCode | 3 | 255 | 251 |
| production | WorkAssistant.Tools | 42 | 5715 | 5389 |
| tests | WorkAssistant.Tests.Console | 12 | 2388 | 2193 |

| 汇总 | 文件数 | 物理行数 | 非空行数 |
|---|---:|---:|---:|
| production | 142 | 18396 | 17459 |
| tests | 12 | 2388 | 2193 |
| build | 10 | 870 | 805 |

明细见 SOURCE_LINES.json。复算：`python scripts/Count-SourceLines.py`。
