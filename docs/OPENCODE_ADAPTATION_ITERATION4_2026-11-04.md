# 第四轮：项目角色、技能与命令

日期：2026-11-04。候选入口：`C:/files/generate/gui_cli/artifacts/iteration4-candidate/WorkAssistant.Desktop.exe`。第三轮目录保留。

参考本地 OpenCode v1.1.20，提交 `09ff3b9bb925903b36aa35fec25394133a42cf6d`：`agent/agent.ts`、`config/config.ts`、`skill/skill.ts`、`session/system.ts`。继续使用 C# 本地内核和隔离 Worker；没有引入 Bun/Node 运行时或远程内核。MIT 声明随包保留。

## 已实现的行为

主 Host 启动时读取当前工作区的项目配置。角色、说明、命令和项目技能正文冻结一次，主、子 Agent 共用该次快照；配置原文保存至本地状态库任务目录的 `configuration/project.json`。TaskRecord 保存 `AgentRoleId`、`ProjectConfigHash`、`CommandName` 和 `ExpandedRequest`，保留原始 `UserRequest`。子任务保存同一个配置哈希，可沿 ParentTaskId 回查主任务快照。

该哈希覆盖本轮角色、技能、命令及选中的项目说明文件，不是整个应用配置、工作区输入或模型凭据的哈希。原有工作区权限、模型目录、输入清单及输出哈希仍走各自机制。

| 入口 | 本轮支持 |
|---|---|
| 工作区根 `AGENTS.md` / `CLAUDE.md` | 优先 AGENTS.md，存在时不加载 CLAUDE.md；不向父目录或用户目录递归查找 |
| `.claude/agents/*.md` | 自定义角色；兼容 `.claude/agent/*.md`；当前仅支持单层文件 |
| `.claude/skills/<name>/SKILL.md` | 标准 Markdown 项目技能，无需 skill.json；ID 为 `project-<name>`，不覆盖内置或个人技能 |
| `.claude/commands/*.md` | 用户输入 `/命令 参数`，纯文本替换 `$ARGUMENTS`；不含占位符时在正文后追加参数 |
| `/agent <role> <request>` | 显式选择 primary/all 角色；保留用户原输入，向模型发送解析后的请求 |
| `delegate_tasks.tasks[].subagent_type` | 模型选择 subagent/all 角色；默认 general；整个批次先校验，角色无效时不创建任何子任务 |
| `list_project_commands` | 项目存在命令或自定义角色时才注册，返回命令和可见主角色；只查询，不执行 |
| 技能工作室 | 显示项目技能与来源，允许选用；另存为个人技能不修改项目文件 |

预置角色：`general`（all，可读写）、`explore`（subagent，只读）、`review`（subagent，只读）、`plan`（primary，只读）。内置名保留；重复配置明确报错。`hidden` 只控制发现目录，不构成权限边界；`disable` 禁止使用。

权限按 **工作区规则 ∩ 已选技能 ∩ 主角色 ∩ 子任务工具上限 ∩ 子角色 ∩ 执行模式** 收紧。所选技能清单以外保留提问、技能/命令发现、任务计划与历史等元工具，再由工作区与角色继续约束。allow 不能恢复父级缺失的工具，不能消除已有审批。只读角色不提供生成成果或修改文件的能力。读取技能正文仅提供方法，不能动态授予权限；选择技能运行时，allowed-tools 才用作该次主任务的工具约束。修复了原有选中技能后又无条件添加 delegation/import 的权限空隙。

子任务仍最多两并发、每轮四个；保留独立文件快照、输出目录、依赖 DAG、审批关联、取消和哈希交接。角色步骤/工具/超时只可进一步降低默认上限（子任务 8/12/180 秒，主任务 24/48/600 秒）。当项目有技能时，子任务可在父权限范围内按需 list_skills/read_skill，正文也来自同一份快照。

## 可直接使用的配置

在工作区创建 `.claude/agents/auditor.md`：

```markdown
---
description: 只读核对文件内容和证据
mode: all
model: worker
read_only: true
steps: 5
tool_limit: 6
timeout_seconds: 90
tools: [read_document_text, search_input_text, read_skill]
permission:
  '*': allow
---
逐项核对来源，列出依据和不确定项，不生成或修改文件。
```

输入 `/agent auditor 核对当前输入材料` 即可。主 Agent 也可以在 delegate_tasks 中使用 `subagent_type: "auditor"`。

`model` 仅支持 `inherit`、`main`、`worker`，引用软件中已经配置的模型；不会从项目配置导入 API Key、端点或新的服务商。根任务 inherit 使用主模型，子任务 inherit 使用子模型（未另设时跟随主模型）。命令的显式 model 优先于角色 model；派发参数 model_alias 优先于子角色 model。

创建 `.claude/commands/audit.md`：

```markdown
---
description: 按核对角色审查材料
agent: auditor
---
请核对以下任务，并列出证据：
$ARGUMENTS
```

输入 `/audit 检查合同日期是否一致`。命令的 agent 必须是启用的 primary/all 角色；`/agent` 名称保留。

创建 `.claude/skills/evidence/SKILL.md`：

```markdown
---
name: evidence
description: 按来源核对材料
allowed-tools: [read_document_text, search_input_text, read_skill]
version: '1.0'
---
先定位相关段落，再按文件与行号逐项核对；不要把缺失信息当成事实。
```

可在技能工作室选用，也可由模型按需读取 `project-evidence`。

## 配置语法与限制

Frontmatter 支持 JSON 对象，或明确受限的 YAML 子集：标量、布尔、整数、引号字符串、二空格缩进对象、字符串列表、`|`/`>` 多行说明。支持工具列表及布尔工具对象；布尔对象中只有显式 true 的工具进入该角色清单。permission 支持 allow/ask/deny 字符串默认值，或原生工具名与 `*` 的对象。不存在路径 glob 或 Bash/Read 等外部工具名映射。

角色字段：name、description、mode、model、tools、permission、hidden、disable、read_only、steps、tool_limit、timeout_seconds。name 若提供须与文件名一致。技能字段：name、description、allowed-tools、version。命令字段：description、agent、model。未知字段、重复键、错误类型和无效角色均报错；不会静默吞掉权限字段。裸标量包含冒号空格、行内注释时应加引号；不支持通用 YAML 的锚点、标签或复杂对象。

名字为 2～40 个小写字母/数字/连字符、字母开头。单文件最多 64 KiB、共最多 128 个读取文件、总量 512 KiB；正文最多 30000 字符（根项目说明受文件大小限制）。文件句柄验证最终路径和硬链接数；目录锁定并拒绝 junction/reparse，防止通过链接读取项目边界外的配置。

明确未实现：完整 OpenCode 配置继承/任意 provider/model 选择、全量 YAML、嵌套角色目录、命令 shell/文件引用展开、技能脚本和附属资源执行、递归子 Agent、任意源码编辑、终端、MCP/LSP。项目技能附属文本资源读取也留待后续实现；当前仅读取 SKILL.md。未实现项不应通过配置字段伪装启用。

## 代码交接

| 源码 | 职责 |
|---|---|
| Core/AgentRole.cs | 单向权限收紧、审批继承与执行上限 |
| Infrastructure/MarkdownConfiguration.cs | 有界 Frontmatter 解析和字段类型校验 |
| Infrastructure/ProjectConfigurationFiles.cs | 句柄与目录锁、链接拒绝、读取总量限制 |
| Infrastructure/WorkspaceAgentConfiguration.cs | 项目角色/技能/命令、说明优先级、快照与请求解析 |
| Host/Program.cs、DelegateTasksTool.cs | 主/子模型别名、执行模式、工具交集和冻结配置 |
| Host/SkillReferenceTool.cs、ProjectCommandsTool.cs | 按需读取冻结技能、发现命令 |
| Core/AgentController.cs、TaskRecords.cs、Infrastructure/LocalTaskStore.cs | 原始与展开请求、角色及配置来源持久化 |
| Desktop/SkillLibraryWindow.cs | 项目技能来源与完整工具标签；修复原标签数组越界 |

## 验证

Release 构建 0 警告、0 错误。本轮 49 项检查覆盖解析、权限、身份、持久化、原文保留、配置冻结、大小/数量/链接边界、真实 Host/Worker 与无窗口 WPF 渲染。实际验证了角色切换到子模型、子角色切换到主模型、只读审查、审批后生成 Word、无效角色整批拒绝、所选技能不越权。模型端为本机合成 HTTP 服务，没有访问真实用户 API。

前三轮 188 项专项检查及 13 个旧 HTTP/SSE 用例继续回归。旧用例的本机监听在受限沙箱里出现平台限制后，在正常本机执行环境重跑；不以沙箱报错当作代码缺陷或测试通过。Win7 实机、真实厂商 API、长时间压力及完整 GUI 人工验收仍需单独进行。

复现入口：`artifacts/diagnostics/iteration4/http_integration.py`；其 verify.ps1 构建诊断并核对加载 DLL 哈希。第三轮诊断现支持 XIAO_CANDIDATE/CandidatePath，可指定第四轮目录，避免测到旧二进制。测试文件和报告在 artifacts 中，不计入生产代码行数。
