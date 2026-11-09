---
name: github-docx
description: "从 MIT Word 技能适配的默认流程：编写、修改修订副本、回读校对新闻稿、教案与正式文档。"
---

本技能改编自 darce07/docx-mit-claude-skill（MIT），执行环境适配为晓助手本地 C# 工具。它提供工作方法，不新增权限；不要求 Python、终端或 Office COM。只读和只规划请求优先，不主动生成用户没有要求的文件。

## 按任务选择路径

- 阅读与问答：inspect_office(path=file_id) 获取分页正文和段落编号，必要时用 read_document_text/search_input_text；仅回答问题时不强制导出文件。
- 自由编写：write_word(title, blocks, file_name)，段落、小标题、项目和表格分别使用 paragraph/heading/bullet/table。不依赖 aggregation_id；不要为了生成文稿伪造数据表。
- 修改原稿：先 inspect_office 取得真实 source_sha256、paragraph_index、expected_text，再 edit_word 输出新副本。默认 track_changes=true，交付修改清单。新文字继承首个文字片段样式，不宣称完整字符级格式不变。
- 既有占位符模板：先 preview_docx_fields，再 fill_docx_template；没有字段时不虚构模板参数。

新建与正文结构细节：用 read_skill(id="github-docx", resource="references/authoring.md")。
原稿修改、修订与校对：用 read_skill(id="github-docx", resource="references/edit-review.md")。

## 生成后回读

调用 inspect_office(artifact_name=刚生成的文件名)，不要传物理路径；查看 proofread_findings，按 next_item/has_more 阅读所有相关页。疑点只是启发式提醒，零疑点也要通读。核对人名、日期、数字、否定词、遗漏段落、表格列数、乱码、占位符和语义是否改变。发现真实问题，使用新的版本名重新生成；不能默默删除有意义的重复词或待确认标记。

结构校验不等于页面渲染。当前 render_verified=false 时明确说明页面版式未在用户电脑实际渲染，不声称更新了目录页码或验证了空白页。页眉页脚、自动目录、图片/图表插入、批注和复杂控件编辑尚无对应工具，不通过 shell/脚本绕过限制。保留用户材料中的不确定性，不编造事实与引用。

来源、提交与改动记录见同目录 source.json；MIT 声明见 LICENSE.txt。


## 本地文档工作台

先用 document_workspace plan 登记全部预期成果，begin 建立文档，append 按小章节保存，inspect 回查与 patch 校订，最后 finalize 交付。每批建议不超过12块/2500字或3页PPT。保留 document_id、revision、section_id 和 operation_id；失败后继续已保存草稿，不要求用户手动拆稿。同名修订保留内部旧版本，只交付当前成果。
