# 第五轮：文本编辑提案与版本冲突预检

日期：2026-11-04。候选入口：`C:/files/generate/gui_cli/artifacts/iteration5-candidate/WorkAssistant.Desktop.exe`。保留第四轮目录。

本轮对应 OpenCode 固定基线 v1.1.20 的 `tool/edit.ts`、`file/time.ts` 中的精确替换、变更前版本检查和差异审阅思路，继续使用本地 C# Host/Worker。上游 edit 的模糊匹配、直接写入和 LSP 后处理尚未迁移；本轮只完成提案阶段。20 轮总体计划见 `OPENCODE_MIGRATION_20_ROUND_PLAN_2026-11-04.md`。

## 已实现

`plan_text_edit` 接受当前任务的 file_id、old_text、new_text、reason 和可选 replace_all。读取输入快照与当前原文，先比较字节 SHA-256；源文件已变化则返回 EDIT_CONFLICT，不生成过期提案。默认要求 old_text 唯一精确匹配，含大小写与换行；明确 replace_all 时替换所有非重叠匹配。不同于上游的多种纠错/模糊匹配，本轮有意拒绝不唯一或不存在的匹配。

生成任务级持久提案，包含原文和候选字节的 Base64、前后 SHA-256、变更目的、匹配数量、统一格式文本差异与指纹。提案保存在任务自己的 plans 存储，不能跨任务引用。输出 `plan_<id>.diff` 与 `plan_<id>.candidate.txt` 两个可审阅成果，不覆盖或修改输入原文件。模型只收到最多 6000 字符的差异预览及截断标志，完整差异随成果保留。

`validate_text_edit` 校验提案内部摘要、指纹和差异，再重新读取当前原文检查基线。原文变化、被移动或删除时返回 EDIT_CONFLICT。控制器在冲突后停止本轮，不继续请求模型、不自动覆盖、不把已有候选当成已完成的原文修改；保留候选供核对。预检通过仅代表检查时的状态，不能作为未来提交或审批资格。

成功生成提案时，最终答复增加明确说明：原文未修改，尚未审批或提交。提案成果可以完成“生成提案”的任务，但不会设置 HasMutationEvidence；文件整理/移动请求仍须真实修改证据，不能拿文本提案代替执行。

## 约束

- 当前仅支持 inputs 下既有的受控文本/源码扩展名；不支持任意项目目录、Office/PDF/二进制、新建文件或删除文件。
- 文件前后均不超过 256 KiB；替换文本分别最多 16000 字符，reason 最多 500 字符，最多 1000 个非重叠匹配。
- 支持严格 UTF-8（有/无 BOM）及有 BOM 的 UTF-16 LE/BE，保留编码/BOM。未修改区域字节保持；替换文本按参数原样编码，不自动猜测换行。
- 拒绝无效编码、UTF-32、二进制控制字符和仅改变换行格式的提案。文本 diff 统一使用 LF 展示，候选字节与 SHA-256 才是准确内容；它不是可无条件导入的任意 patch 协议。
- 输出文件后缀固定为 `.candidate.txt`，不执行候选代码。提案不是批准，不调用旧 apply_file_plan 来写文本。
- 生成提案会写任务成果，因此只读模式不提供 plan_text_edit；validate_text_edit 可在只读模式使用，但仍受工作区、角色和技能权限限制。
- 子 Agent 的办公工具白名单本轮没有扩大，暂不直接生成/提交文本提案；第 11 轮完成更完整的提案归属与合并协作。当前其他 Agent 或用户对原文的更新会被哈希预检识别。

## 工程变更

| 文件 | 职责 |
|---|---|
| Core/TextEditProposal.cs | 有界精确替换、编码保持、差异、SHA-256/指纹及完整性校验 |
| Tools/TextEditTools.cs | 输入读取、提案持久化/成果提交与源版本预检 |
| Core/Interfaces.cs | FilePlanRecord 的可选 TextEdit 载荷；保留原构造函数 |
| Tools/PersistentFilePlanStore.cs | 跨 Worker 提案恢复；计划读取改为句柄校验、2 MiB 与 JSON 深度/重复键限制 |
| Tools/FilePlanTools.cs | 内存存储保留文本载荷；旧移动执行工具明确拒绝文本提案 |
| Core/AgentController.cs、SystemPrompts.cs | 提案与提交证据区分、冲突立即停止及最终说明 |
| Tools/AskUserTool.cs、Core/SkillDefinition.cs | 注册两个原生工具，纳入统一角色/技能/工作区权限 |
| Tools/SafePreview.cs | 在现有成果预览中显示 diff，并按 BOM 解码 UTF-16 候选文本 |

新增 EDIT_CONFLICT 错误码；通用未识别工具名不能绕过现有分发器。原 ToolContext、FilePlanRecord 的构造入口未变，不因增加新服务参数破坏旧二进制调用方。只读主工具数量相应增加一个验证工具；生成提案仍不可见，回归断言同时检查数量和工具语义。

## 验证与交接

Release 构建 0 警告、0 错误。本轮 44 项检查：编码/BOM、换行、唯一/全部匹配（含重叠歧义）、大小与路径、提案篡改、跨任务隔离、旧工具拒绝、原文不变、diff/UTF-16 成果预览、独立 Worker 恢复、实际并发外部更新与冲突停止。模型服务是本机合成 HTTP API，没有使用真实模型凭据或业务文件。

回归涵盖原生内核 36、子 Agent 核心 26、子 Agent HTTP 54、第二轮 22、第三轮 50、第四轮 49，以及旧 HTTP/SSE 13 个用例。第五轮新增后的总检查为 294；以候选目录 verification 的实际报告为准。Win7 实机、真实厂商和长时间压力仍未验收。

复现：`python artifacts/diagnostics/iteration5/http_integration.py`。旧轮诊断通过 XIAO_CANDIDATE/CandidatePath 指定第五轮；第四轮诊断本次补齐参数支持。各诊断编译后核对实际加载 DLL，避免误测旧包。需要本机监听与命名管道的检查在正常本机环境执行。

下一轮明确增加审批绑定、锁内基线复验、原子提交、备份及条件回滚。成功预检和提案不能跳过这些步骤。多文件修改、通用源码树和终端按 20 轮路线图分阶段推进。
