# 第七轮 办公文档自动化

后续追加：GitHub 默认办公技能、自动选择与成果回读校对已交付到 `artifacts/iteration7-skills-candidate`，详见 [GitHub 办公技能迁移](GITHUB_OFFICE_SKILLS_MIGRATION_2026-11-04.md)。下文保留第七轮基础实现与验收记录。

日期：2026-11-04。候选入口：`C:/files/generate/gui_cli/artifacts/iteration7-candidate/WorkAssistant.Desktop.exe`。保留第六轮目录。

按本轮用户要求，把新闻稿、教案和 PPT 作为实际交付目标。新增通用文档执行工具和三个办公技能，完成“内容组织 → 生成文件 → 检查原稿 → 修改新副本 → 交付成果”的基础闭环。此轮为本地办公扩展，不冒充 OpenCode 上游已有办公能力的逐字段移植。

## 已实现的场景

| 场景 | 实际交付 |
|---|---|
| 新闻稿编写 | 根据用户材料编写标题、小标题、段落，输出真正的 DOCX，不再依赖 aggregation_id |
| 新闻稿修改 | 读取原稿段落定位与哈希，精确改写纯文本段落，默认保留插入/删除修订，输出新 DOCX 和修改清单 |
| 教案编写 | 标题、教学目标、过程、评价、作业与反思，可包含教师/学生活动和时间表格 |
| 教案优化 | 按原稿段落及表格单元格中的纯文本段落修改，保留原件；内容方法由教案技能约束 |
| PPT 编写 | 生成可编辑 16:9 PPTX，含封面、内容页、页码、主题/母版与逐页讲者备注 |
| PPT 优化 | 读取实际页面、普通文本形状和段落，精确改写文字，输出新 PPTX 与修改清单，保留未修改部件 |
| 阅读与预览 | DOCX/PPTX 分页检查；PPT 可通过原有正文阅读/搜索链路读取，界面提供明确标注的文字预览 |

新增内置技能：新闻稿编写与修改（news-editor）、教案编写与优化（lesson-planner）、PPT 编写与优化（presentation-author）。原“文档与模板”技能升级到 1.2，支持直接编写和修改 Word 副本。技能要求核对事实、标注待确认信息，不捏造数字、引语、课程标准或教学效果。

## 使用

从本候选目录启动，在技能工作室选择对应技能，再附加资料或原稿。例如：

- 新闻稿：根据附加材料编写一篇校园活动新闻稿，输出 Word，缺失事实标注待确认。
- 原稿修改：修改附加新闻稿的导语和结构，保留原文事实与 Word 修订记录，输出新版本。
- 教案：编写一课时的阅读教学教案，包含目标、活动、评价及教学过程表格，输出 Word。
- PPT：把附加材料整理成六页演示文稿，每页有简短要点和讲者备注，输出 PPTX。
- PPT 修改：优化附加 PPT 中的标题和正文表达，保留未修改页面及其他部件，输出新版本。

将资料作为文件附加到 inputs，模型使用任务清单 file_id，不需要手动写物理路径。GUI 文件选择器和实际导入白名单已统一，支持 PPTX，也修复了原先文本格式在选择器可见却被导入拒绝的问题。技能工作室新增工具的名称显示为中文。

## 工具接口

| 工具 | 主要参数 |
|---|---|
| inspect_office | path=file_id；start_item/max_items；返回源 SHA-256、分页与段落定位 |
| write_word | title、file_name、blocks；块为 heading/paragraph/bullet/table，table 使用等列数 rows |
| edit_word | path、source_sha256、file_name、edits；每项 paragraph_index、expected_text、new_text；track_changes 默认 true |
| write_presentation | file_name、slides；每页 title，及可选 layout=cover/content、bullets、notes |
| edit_presentation | path、source_sha256、file_name、edits；每项 slide_number、shape_id、paragraph_index、expected_text、new_text |

修改前必须检查原稿，使用实际定位、哈希和精确原文。多个修改先在内存验证，全部通过后才提交新成果。错误定位、过期哈希、重复定位或不支持的复杂段落均拒绝，不猜测修改位置。输入原件和快照均不覆盖，因此这些工具是输出副本类，不复用第六轮的原文事务提交。

Word 默认使用中文字体、居中标题、正文行距和小标题，表格有内边距、表头底色和跨页重复表头。PPT 使用统一的浅色内容页/深色封面；对生成标题、要点密度做容量约束，已知文本框的修改做估算预检。文字太密时要求模型拆页，不截断内容。

## 真实支持边界

本轮完成的是上述基础办公文档自动化，不是替代 Word/PowerPoint 的全部编辑功能。

- Word 修改保留未修改 ZIP 部件的字节；目标段落的新内容继承首个文字片段样式，并保留段落属性。不能宣称混合字符样式、分页位置和原稿全部复杂格式都保持不变。
- 修改目标仅为纯文本段落；包含域、链接、图片、控件或已有修订的目标段落会拒绝。其他未修改部件保留。页眉、页脚、批注没有作为本轮可编辑目标。
- PPT 修改限普通文字形状，不含图表、SmartArt、图片文字识别、复杂动画、主题替换和任意页面重排。已有复杂部件不被重写，也不保证其与新文字布局自动协调。
- 新 PPT 为文字版式；没有自动配图、生成图表或在线素材检索。讲者备注支持生成，现有备注的精确编辑未开放。
- 最大输入 Office 文件 20 MiB，同时受输入快照单文件 10 MiB 上限约束。ZIP 解压、部件数量、XML 实体与外部关系继续受现有策略约束。宏、嵌入对象与外部关系不接受。
- Word 最多 200 个正文块、6 万字符，表格最多 80 行 6 列；PPT 最多 40 页，内容页最多六点；单次修改最多 50 项。模型/IPC 上下文的更小限额同样生效。
- 只读模式仅提供 inspect_office 和原有阅读能力，不提供文档生成或修改副本工具。新增办公写入未扩大子 Agent 原有工具白名单；主 Agent 仍可汇集子任务分析后生成文档。
- 软件运行时做包结构、正文和有界文字容量检查，返回 render_verified=false。应用尚未接入用户电脑上的自动逐页原生渲染。文字预览不是实际版式预览，不能把开发机样例验收推广为所有生成文件都已完成渲染验收。

生成与修改使用纯 C#/.NET 4.8 OOXML 引擎，用户运行这些工具不需要 Python，也不要求安装 Office。打开成品需可兼容 DOCX/PPTX 的应用；本轮验证使用当前机器上的 Microsoft Word/PowerPoint，未承诺所有 WPS/Office/Win7 版本的实际版式一致。

## 代码与依赖

| 源码 | 职责 |
|---|---|
| Tools/OfficePackage.cs | 被动 OOXML 包读取、主关系/内容类型、段落与幻灯片定位、源哈希 |
| Tools/WordAutomation.cs | 自由正文、教案表格、中文样式及段落修订 |
| Tools/PresentationAutomation.cs | 幻灯片、备注、容量检查与保留其他部件的文字修改 |
| Tools/OfficeAutomationTool.cs | 五个 Agent 工具、参数验证、任务成果和修改清单 |
| Tools/Assets/PresentationBase.pptx | 嵌入的空 PPTX 母版/布局/主题底座，无运行时 Python 依赖 |
| Tools/DocumentTextTools.cs、SafePreview.cs、TableReading.cs | PPT 阅读/文字预览及输出校验 |
| Core/SkillDefinition.cs、SystemPrompts.cs | 工具注册范围、文档任务方法与完成证据提示 |
| Desktop/MainWindow.xaml.cs、SkillLibraryWindow.cs | PPT 导入入口和中文工具名称 |
| skills/news-editor、lesson-planner、presentation-author | 新闻、教学和演示文稿工作方法 |

PPTX 底座在开发阶段由 bundled python-pptx 1.0.2 生成，相关 MIT 声明放入 third-party-licenses；不把 python-pptx 作为客户端运行依赖。Open XML 结构参考：[PresentationML 结构](https://learn.microsoft.com/en-us/office/open-xml/presentation/working-with-presentations)、[Word 插入修订](https://learn.microsoft.com/en-us/dotnet/api/documentformat.openxml.wordprocessing.insertedrun)。

## 验收

Release 构建 0 警告、0 错误。新增 46 项检查，覆盖自由生成、修订、表格、分页定位、源版本、重复编辑、保留未修改部件、过密文本拒绝、取消、宏/外部关系与主关系损坏；实际合成模型 API → Host → 隔离 Worker → Office 成果链路通过。原有 344 项检查继续回归，总计 390 项。

在本机真实 Word 打开并导出新闻稿、带两条修订的修改稿、教案，各一页；在 PowerPoint 打开三页课件、三页修改稿和一页空正文标题页，导出共七页图片，未检测到样例文本框溢出。共六份样例、十页实际页面已检查；已修正教案表格内边距。office-render.json 记录每份被渲染文件的 SHA-256，成品样例与检查证据随候选交付。

这些都是合成材料，不对应真实学校或活动，没有使用真实模型凭据。真实模型内容质量、长文档/复杂模板、其他 Office 版本、Win7 实机和长期压力仍需后续验收。

复现：`python artifacts/diagnostics/iteration7/http_integration.py`；实际 Office 渲染：`powershell -File artifacts/diagnostics/iteration7/render-office.ps1`（仅在已安装 Word/PowerPoint 的验收机上运行）。原生渲染是开发验证脚本，不是已注册的 Agent 工具。最终样例位于候选的 samples/office，报告位于 verification。
