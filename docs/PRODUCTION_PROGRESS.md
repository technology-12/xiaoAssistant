# 1.0 正式版实施进度

> 更新：以下保留 2026-10-31 功能增量的历史记录。当前实现以 [2026-11-01 逐包修复记录](PRODUCTION_FIXES_2026-11-01.md) 为准；旧文中的 JSON 活跃存储、600 字上下文和完成证据描述已被新实现替代，历史测试数量不代表本轮修复已验证。

日期：2026-10-31（晚）· 基线：`docs/PRODUCTION_IMPLEMENTATION_PLAN.md`（1.0 规划稿）
本文件记录第一个完整功能增量实际落地了什么、验证到什么程度、还差什么。**不宣称 1.0 已发版。**

## 本增量交付内容（对应计划工作包）

### W02 契约与任务类型 —— 已落地

- `TaskKind`（QuestionAnswer / DataAnalysis / FileGeneration / FileModification）与按类型的完成证据：
  - 问答/分析轮：完整文本回复即成功，**不强制生成文件**（计划 §4.1、§5.5）。
  - 文件生成/修改：必须有产出类工具成功执行且成果登记；模型“声称完成”→ `NO_ARTIFACTS`。
  - 用户拒绝审批的产出操作 → `USER_APPROVAL_DENIED`，不因计划成果标成完成。
- 新增状态：`WaitingForUser`（审批等待）、`CompletedWithWarnings`、`NeedsReview`（文件整理部分完成，不自动重试）。
- 工具元数据：`ToolDefinition.ProducesArtifacts / RequiresApproval`（计划 §8.1）。
- 提示词按任务类型与轮次组装（`SystemPrompts`），不再要求所有任务导出销售汇总。
- 新错误码：`USER_APPROVAL_DENIED / UNSUPPORTED_FORMAT / PLAN_INVALID / NEEDS_REVIEW`。

### W05 模型与对话 —— 已落地（真实服务联调除外）

- **连续对话**：Session 与 Task 分离（`SessionRecord`，存储于任务库 `_sessions/`）。
  后续轮次注入此前轮次的 user/assistant 摘要（600 字截断 + 成果路径），
  每轮写入独立 `outputs/<task_id>/`，不覆盖上一版（§5.4）。
- **SSE 流式**（§7.2）：`IStreamingModelClient`；工具调用片段按 index/ID 聚合，
  收到结束信号才组装执行；断流 → `MODEL_UNAVAILABLE`（已输出过增量不重试）；
  端点返回非事件流自动回退非流式。UI 增量渲染（AssistantDelta 节流 250ms）。
- **多 Profile**（§7.1）：`ModelProfile` 列表 + 活动配置；API Key 按 Profile 独立 DPAPI 密文；
  旧版单配置自动迁移（含凭据迁移）。
- 用量（usage）服务端返回时累计入任务记录；缺失显示未知。

### W06 办公工具 —— 已落地（纯托管实现，无 native 依赖）

- **XLSX 读**：共享字符串 / 内联字符串 / 数值 / 公式缓存值 / 布尔；首工作表；列对齐；限制同 CSV。
  已知限制：日期为 Excel 序列值。
- **XLSX 写**：最小部件集（无宏/外部链接），内联字符串 + 原生数值单元格，表头加粗样式，
  **确定性输出**（同输入同字节）。
- **DOCX 生成**：受控文档模型（标题/二级标题/段落/项目符号/表格），
  数字全部来自聚合结果；模型只能提供纯文本标题与说明（§8.3 不接受任意 XML）。
- **DOCX 模板**：`{{字段}}` 填充（输出为模板副本）；缺字段/剩余占位符明确报错；`preview_docx_fields` 列字段。
- **PDF 提取**：literal / hex 字符串、Tj/TJ/' 操作符、FlateDecode（zlib）、ToUnicode CMap（bfchar/bfrange
  含数组形式、中文验证）、按页输出并保留页码；扫描件/无文字明确区分并报 `UNSUPPORTED_FORMAT`；
  已知限制：多字体 CMap 按全文档合并、LZW/DCT 等过滤器跳过并计入扫描件判定。
- **aggregate_table**：CSV/XLSX 同管线 + `where` 筛选 + `group_mapping` 映射；口径（筛选/映射/排除数）
  写入聚合结果并出现在 csv/md/docx 成果中（§7.3 口径可追溯）。
- **文件整理**（§8.4 四步）：`plan_file_organization`（校验：仅 move、inputs 内、无覆盖、无重名、无删除）
  → 审批门 → `apply_file_plan`（预检 → 逐项移动 → 校验源消失/目标存在 → file_mutations.md 变更清单）；
  部分失败 → `NEEDS_REVIEW`，宿主进入 NeedsReview，不自动重试。

### W07 GUI 正式流程 —— 已落地（沿用 V3 视觉）

- 连续对话视图：多轮消息/步骤/成果按轮次组织；流式助手条目增量更新、中断标注“内容可能不完整”。
- 任务队列：运行中 `Ctrl+Enter` 入队（写工具串行，§7.4），显示“第 N 位”。
- 审批对话框：逐项列出“源 → 目标”，同意/取消；任务取消时自动关闭并视为拒绝；
  演示模式自动通过（事件中仍可见审批步骤）。
- 历史改为**会话维度**：标题=首请求，状态=轮次聚合（完成 N 轮 / 部分失败 / 待复核…），回看重建全部轮次。
- 设置窗口：Profile 管理（新增/删除/切换）、按 Profile 凭据、测试连接（含 HTTP 明文提醒）。
- 新快捷任务：CSV 汇总 / Excel 汇总 / Word 报告 / PDF 提取 / 文件整理。

### 验证证据（本机 Windows 11）

- 控制台测试 **61/61 通过**（原 33 项全部保留 + 新增 28 项：XLSX roundtrip/确定性/坏文件、
  aggregate_table 筛选映射、DOCX 生成/模板/字段、PDF 双页/CMap/扫描件/非 PDF、
  问答成功、无产出失败、会话两轮独立输出、会话存储、整理批准/拒绝/冲突 NeedsReview/计划校验、
  Profile 多配置/迁移、SSE 增量/片段聚合/断流/回退/stream 标志）。
- UI 自测 **42/42 通过**（`--selftest-ui`；含 5 轮连续会话：CSV→筛选→问答→XLSX→整理→会话回看→新建重置）。
- 实际 WPF 渲染三场景截图：`docs/previews/v1-home.png` / `v1-completed.png` / `v1-drawer.png`。
- 旧版产物备份于 `artifacts/release-before-v1/`。

## 尚未完成（按计划工作包）

| 项 | 计划位置 | 状态 |
|---|---|---|
| Win7 实机验证（字体/DPI/TLS/性能/x86） | W01 | **未做**——仍以 Win11 本机验证为准，不宣称兼容通过 |
| 真实公网/IP 网关模型实测（含证书矩阵） | W01/W05 | 未做；协议层全部 Mock 验证 |
| SQLite 状态库、迁移与备份（现为 JSON/JSONL） | W04 | 未做；会话/任务文件级存储可用 |
| 独立任务宿主进程 + IPC（现为同进程） | W03 | 未做；取消/预算/中断标记已就绪 |
| 输入快照、句柄级最终路径校验（现为字符串边界 + ReparsePoint 逐级检查） | W9.1 | 部分（TOCTOU 窗口仍在，非对抗性单用户范围内） |
| opaque file_id 显式文件引用 | §9.1 | 未做（工具仍用授权根内相对路径） |
| 安装包/签名/更新器 | W08 | 未做 |
| 50 个固定业务任务回归集、长时运行、故障注入 | W09/W16 | 未做（当前 61+42 项） |
| xlsx 日期格式、扫描件 OCR、PPTX、COM | 1.1 | 明确排除 |

## 风险与后续建议

1. **真实模型联调**是下一步最高优先级（W01）：SSE/工具调用差异、usage 字段、重试计费语义需实测校准。
2. XLSX/PDF 解析为自有实现，覆盖常见生成器（Excel/WPS 写出的 xlsx、带 CMap 的文本 PDF）；
   未见过的边角格式会以 `UNSUPPORTED_FORMAT` 明确失败而不是损坏数据——试点期建议收集实际样本扩充测试。
3. 会话存储与会话内上下文压缩目前为文本摘要；超长会话应增加轮数/字节上限（当前仅受
   `MaxConversationChars` 512 KiB 总预算保护）。
