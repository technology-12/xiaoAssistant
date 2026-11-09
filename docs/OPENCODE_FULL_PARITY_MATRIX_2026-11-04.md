# OpenCode 全量功能对齐矩阵

日期：2026-11-04。源码基线：本地 OpenCode v1.1.20 / `09ff3b9bb925903b36aa35fec25394133a42cf6d`，不是对其后所有上游版本的兼容声明。对照 src 下模块与 tool 目录，已实现不等于完整等价。

目标是在本地 C#/.NET 4.8 与 Win7 约束内逐项移植行为和协议，模型可调用外部 API。上游源码体积不计入自研规模；现有 WorkAssistant.OpenCode DLL 仍为历史远程桥接，并非这些迁移能力的来源。

| 上游能力/入口 | 当前状态 | 差距与下一步 |
|---|---|---|
| session/prompt、processor：模型/工具循环 | 已覆盖主要本地链路 | 工具串行、事件与回执具备；缺上游全部钩子、交互及处理分支 |
| tool/task、agent：子 Agent | 部分适配 | 独立上下文、父子记录、模型、取消、权限及项目自定义角色已具备；缺上游递归任务和完整角色配置语义 |
| 批量/依赖协作 | 项目扩展 | 两并发、每轮四个、依赖 DAG 和哈希交接；不声称与上游 batch/task 完全一致 |
| permission/next | 部分适配 | allow/ask/deny、审批请求关联；缺完整路径模式、remember/always 范围与外部目录权限 |
| provider：Chat Completions | 文本/客户端工具已适配 | 缺完整提供商变换、reasoning_content 等推理元数据的可靠重放 |
| provider：Anthropic Messages | 第三轮增加文本/客户端工具 | 支持 JSON/SSE 与用量；thinking 签名、媒体、服务端工具等明确未适配 |
| provider：Responses/Gemini/Azure/Bedrock/Vertex等 | 待实施 | 不用 Chat Completions 冒充专有协议；需独立认证、消息转换与异常检查 |
| provider/models：目录、限制与成本 | 部分适配 | Profile 限制和本地 provider/models/limit 子集可用；缺完整 models.dev 快照、价格、模态及 SDK 选项 |
| session/retry | 已覆盖常见失败链路 | 有限退避、厂商时间提示、取消、可观察性；保留本地限时，与上游最大等待策略有差异 |
| session/compaction、prune | 部分适配 | 模型窗口、输出预留、旧工具投影、摘要和超限重试可用；估算及资源限制有差异，缺全部插件扩展 |
| storage、bus、session事件 | 本地替代实现 | SQLite/命名管道/回执；不是上游事件与存储 schema 的逐字段克隆 |
| session恢复与重放 | 部分适配 | 状态和可验证成果可恢复；写入不确定时待复核，未提供任意检查点自动续跑 |
| tool/read、ls、glob、grep | 办公范围部分覆盖 | 支持输入快照、文本分页/搜索、表格文档；缺全项目任意源码读取、glob 和正则 grep 等完整语义 |
| tool/write、edit、multiedit、patch | 第六轮增加单文件提交子集 | inputs 文本提案、差异、哈希预检、逐次批准、NTFS 事务提交及本任务条件回滚具备；需可用的本地 NTFS 事务；项目树、多文件 patch、跨任务撤销仍待后续，不算通用编辑器完成 |
| tool/bash、shell、pty | 待实施 | 缺受控本地终端与进程会话；Win7 应适配 cmd/PowerShell 等可用执行器 |
| tool/todo、plan、question | 部分适配 | 内部任务计划和主 Agent 提问具备；缺完整计划模式与上游所有交互语义 |
| skill、command、config | 第四轮增加项目子集 | .claude 项目角色、SKILL.md、文本命令、根说明优先级和冻结快照可用；缺完整配置继承、资源/脚本、shell 展开与完整 YAML，见第四轮说明 |
| mcp | 待实施 | 优先本机 stdio MCP，工具发现、资源与权限需纳入本地边界 |
| lsp、format、ide、acp | 待实施 | 缺语言服务、格式化、IDE/ACP 协议；不是现有桌面聊天功能的同义替代 |
| snapshot、patch、worktree、project | 部分适配 | 有项目与输入快照；缺 Git worktree、补丁反转、工程差异及合并恢复 |
| webfetch、websearch、codesearch | 待实施 | 缺对应工具与提供商接入；后续需明确网络授权和本地处理范围 |
| plugin、auth、env | 部分/待实施 | Profile 和 DPAPI 凭据具备；缺插件钩子、OAuth、第三方 SDK 和完整环境配置语义 |
| server API、SDK、CLI/TUI、share | 不等价 | 当前为桌面 UI 与私有本地 IPC；不支持上游客户端直接连接或完整分享服务 |
| 多模态、prompt caching、成本/用量细分 | 部分/待实施 | 已记录服务端 token，Messages 缓存 token 合并；缺媒体消息、显式缓存策略和准确价格核算 |
| 办公文档自动化（本项目扩展） | 第七轮基础链路已实现 | Word 自由编写/修订副本、教案表格、PPT 生成/普通文本修改、办公技能具备；复杂图表/SmartArt、图片/OCR与运行时逐页渲染未实现；不是 OpenCode 全量等价的替代项 |

## 后续优先顺序

1. 提供商剩余关键兼容：推理/签名元数据、更多协议与能力选项。需防止跨厂商错误重放，并验证失败后没有重复执行本地工具。
2. 项目角色和指令层：第四轮已完成受控子集；后续补齐资源读取和更多兼容字段，继续遵守宿主权限交集，不能由模型文本新增权限。
3. 通用文件编辑与终端：第六轮已接入单文件批准和事务提交/回滚；接下来推进项目树、多文件、终端及多 Agent 冲突协作。事务支持与跨任务撤销仍须明确边界。
4. MCP stdio、LSP/IDE 与可恢复工程会话；最后补齐有明确需求的上游服务 API、扩展协议和 UI 能力。

每项以真实行为测试验收，不因同名目录、类名、源码数量或复制上游文件而标成完成。仍保留本机文件执行、Win7 目标、明确审批和禁止不确定副作用自动重放的约束。实际 Win7、真实厂商和长时间运行必须单独验收。

完整轮次、交付条件及第 20 轮完成边界见 `OPENCODE_MIGRATION_20_ROUND_PLAN_2026-11-04.md`。固定基线迁移与整个 OpenCode 托管生态、TUI 外观及所有厂商 SDK 逐项等价需区分，不能按轮数直接宣称全量完成。
