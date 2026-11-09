# OpenCode 适配迭代记录与后续顺序

约束：C# 本地内核、Win7/.NET 4.8 目标；模型计算允许外部 API；文件、调度、权限、持久化和恢复均在本机。固定上游参考：OpenCode v1.1.20，`09ff3b9bb925903b36aa35fec25394133a42cf6d`。扩展机制需要明确与上游原有实现的区别。

每轮交付源码、独立候选目录、可复现检查和交接说明。不得把“已构建”当作实机兼容通过；不得自动重放副作用不确定的写入；不得以代码行数作为可靠性验收指标。

| 轮次 | 状态 | 范围 | 交付 |
|---|---|---|---|
| 1 | 已完成候选实现 | 父子任务、依赖调度、受控成果交接、allow/ask/deny、审批关联 | artifacts/subagent-candidate；OPENCODE_NATIVE_COLLABORATION_2026-11-03.md |
| 2A | 已完成候选实现 | 模型重试、超时分类、重试进度及持久化 | artifacts/iteration2-candidate |
| 2B | 已完成候选实现 | 主/子任务写入中断分类、Job 清理、成果事实恢复 | 同上 |
| 2C | 已完成候选实现 | 成果引用幂等、跨 Host 复用、源与缓存双重校验 | 同上 |
| 3A～3C | 已完成候选实现 | 双协议、能力缓存、模型预算及本地目录 | artifacts/iteration3-candidate；OPENCODE_ADAPTATION_ITERATION3_2026-11-04.md |
| 3D | 待实施 | 提供商剩余关键兼容 | 推理/签名元数据、更多协议与能力选项；详见 OPENCODE_FULL_PARITY_MATRIX_2026-11-04.md |
| 4 | 已完成候选实现 | Agent 角色和项目级配置 | artifacts/iteration4-candidate；项目角色/技能/命令、权限交集、配置快照；字段和限制见 OPENCODE_ADAPTATION_ITERATION4_2026-11-04.md |
| 5 | 已完成候选实现 | 文本变更提案与版本冲突预检 | artifacts/iteration5-candidate；原文不变、差异/候选、编码保持与持久计划；见 OPENCODE_ADAPTATION_ITERATION5_2026-11-04.md |
| 6 | 已完成候选实现 | 审批提交与条件回滚 | artifacts/iteration6-candidate；NTFS 事务、批准绑定、备份/回执、冲突及进程丢失复核；限制见 OPENCODE_ADAPTATION_ITERATION6_2026-11-04.md |
| 7 | 已完成候选实现 | 办公文档自动化（按本轮用户要求调整） | artifacts/iteration7-candidate；Word 新闻稿/教案、修订副本、PPT 生成/文字修改、三项办公技能；见 OPENCODE_ADAPTATION_ITERATION7_2026-11-04.md |
| 8～19 | 已规划待实施 | 编辑/终端/恢复/多 Agent/Git/扩展/协议/桌面 | 逐轮范围和验收见 OPENCODE_MIGRATION_20_ROUND_PLAN_2026-11-04.md |
| 20 | 待验收 | 正式交付与系统验证 | Win7 实机、真实 API、GUI 全链路、长时间运行和故障压力；逐项保留证据，构建签名发行包 |

第七轮已交付上述子集。原第七轮项目树阅读仍未实现，移入第八轮前置工作包，不计为办公功能替代完成。全量差异以 OPENCODE_FULL_PARITY_MATRIX_2026-11-04.md 为准；20 轮目标持续调整工作包，待实施项不算已实现。

第七轮追加 GitHub 办公技能迁移：三个 MIT 来源的 Word/PPT 默认技能、参考资料按需读取、自动选择、当前任务成果回读与启发式校对。候选包 `artifacts/iteration7-skills-candidate`；新增 36 项检查与既有 390 项回归全部通过，合计 426 项。来源、许可及能力边界见 [迁移说明](GITHUB_OFFICE_SKILLS_MIGRATION_2026-11-04.md)。这次追加不代表第八轮已完成。

1.0.1 体验修复已打包：可辨认的默认工作区、输入提示、便携历史隔离、平铺 output 交付、直接预览/打开与像素滚动。候选 `artifacts/usability-candidate`，发行包 `artifacts/packages/XiaoAssistant-1.0.1-portable.zip`，根目录入口“晓助手.exe”。功能/回归 464 项通过，实际 ZIP 两次新解压分别通过 x64/x86 的独立空白数据与中文路径检查；Win7 实机验收未替代。详见 [修复说明](USABILITY_FIXES_1.0.1.md)。
