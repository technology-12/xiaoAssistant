# GitHub 办公技能迁移

晓助手新增三个默认内置技能。这是第七轮办公自动化的补充，不占用第八轮迁移编号。新候选包为 `artifacts/iteration7-skills-candidate`，原有 release 目录未覆盖。

## 来源与许可证

| 内置技能 | GitHub 来源 | 固定提交 | 许可 |
|---|---|---|---|
| Word 文档编辑（GitHub 迁移） | [darce07/docx-mit-claude-skill](https://github.com/darce07/docx-mit-claude-skill) | cd1100a20fa83d2b77fa0405c9298f228e5df214 | MIT |
| PPT 编写与编辑（GitHub 迁移） | [darce07/pptx-mit-claude-skill](https://github.com/darce07/pptx-mit-claude-skill) | ae38be7fff43d1c26cada9db5cfbe7b04e91c4aa | MIT |
| PPT 审阅与优化（GitHub 迁移） | [PeterKalmstrom/claude-powerpoint-skill](https://github.com/PeterKalmstrom/claude-powerpoint-skill) | 130f0d794ffe0b9712ef9e6d8717aa95d90ffc7e | MIT |

逐项保留 LICENSE.txt、source.json 和关键上游文件的 SHA256；汇总见 `migration/office-skills.sources.json`。原始下载位于 `migration/upstream/office-skills`，用于审计，不随应用分发或执行。

调研时也检查了 [Anthropic 官方技能](https://github.com/anthropics/skills)。其 Word/PPT 技能采用独立专有条款（见各目录 LICENSE.txt），本次没有复制或打包。检查 OpenAI 官方 skills 仓库提交 `49f948faa9258a0c61caceaf225e179651397431` 的文件树时，没有找到对应 Word/PPT 技能目录；这不表示其他官方产品没有此类能力。

## 默认使用方式

启动新候选包后，三个技能出现在内置技能列表。用户未手选技能时，Host 按请求自动加载 Word、PPT 或 PPT 审阅的方法指导，识别新闻稿、教案、课件等表达；含“文档”的通用请求可结合已附加的 docx/pptx 类型判断。

明确手选技能优先。“不要使用技能”等请求可关闭自动选择。自动路由只增加指导，不收紧或扩张工具权限；同一任务仍可同时生成 Word 和 PPT。项目权限、只读模式和父子任务的权限交集继续生效。所用技能 ID、版本和手选/自动来源随任务保存。

## 迁移实现

| 上游方法 | 晓助手实现 |
|---|---|
| 先读后改、按目标组织文稿 | read_document_text / inspect_office；Word 内容结构与教案表格指导 |
| Word 生成和修改 | write_word / edit_word，修改输出副本，保留原输入 |
| PPT 内容设计和文字修改 | write_presentation / edit_presentation，提供标题、单页信息量、备注与审阅流程 |
| 按需阅读参考资料 | read_skill(id, resource)，三个技能带中文适配 references/*.md |
| 交付前回读 | inspect_office(artifact_name)，只读当前任务输出，支持分页与内容定位 |
| 基础校对 | C# 启发式检查疑似乱码、重复英文词、标点前空格、未完成占位符；返回疑点，不自动改写 |

资源在技能目录加载时冻结到内存；使用现有安全文件读取器校验路径、链接和大小。read_skill 只能读取已登记资源，不能任意访问磁盘或执行脚本。单技能最多 24 个参考文件，单资源正文最多 24000 字符。路径越界、跨任务成果引用和同时传入输入/输出选择器会被拒绝。

未执行或打包上游 Python、PowerShell、COM、远程错误上报等脚本，也未引入其 Python/PyMuPDF 运行依赖。模型计算仍通过用户配置的 API 完成，文档操作和技能加载在本地 C# 内核执行。

## 能力边界

技能负责方法指导，文档能力由已有 OOXML 工具提供。当前支持可编辑 Word 正文/表格、Word 段落修订，以及可编辑 PPT 文字幻灯片和文字替换。图片、复杂图表、SmartArt、自动目录和精细版式的全量编辑没有因此实现。修改 Word 不保证完整保留段落内所有混合字体样式。

回读与校对不等于页面视觉验收，也不判断事实是否真实。工具明确返回 render_verified=false。PPT 回读不包含备注，备注应与原生成参数比对。当前任务输出可以回读；如需将其作为下一次编辑的输入，须重新附加文件，不伪造 file_id。

## 验证与交付

专项检查覆盖技能来源与许可、默认路由、手选优先、只读权限、混合格式生成、资源冻结/越界、当前任务成果回读和校对疑点。完整结果与实际候选程序集哈希放在候选包 verification 目录。测试只使用本地模拟模型服务和生成的样例，不使用真实 API 凭据或用户文档。

新增专项检查 36 项，既有回归检查 390 项，合计 426 项全部通过；Release 构建 0 警告、0 错误。生产源码统计为 130 个 C#/XAML 文件、17145 物理行（含空行与注释），统计口径见 SOURCE_LINES.md。

Win7 实机、真实模型服务质量和复杂办公文档兼容性仍需后续验收；本次没有将模拟服务结果当作这些验收的替代。
