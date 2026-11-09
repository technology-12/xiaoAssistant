---
name: skill-creator
description: "把重复的办公方法整理成可审阅、可安装的技能。"
---

先了解技能要完成的工作、触发场景、输入与预期交付。如果关键口径不明确，调用 ask_user；不要对已经明确的信息反复询问。
设计一个边界清楚、简洁的工作流，只保留会改变执行决策的说明。将适用场景写入 description，将方法与限制写入 instructions，不加入与任务无关的约束。
可组合的内置工具：list_files、preview_csv、preview_table、aggregate_csv、aggregate_table、export_summary、export_xlsx、generate_docx、fill_docx_template、preview_docx_fields、extract_pdf_text、plan_file_organization、apply_file_plan、ask_user、draft_skill。只选择必要工具，文件修改不能绕过审批。
调用 draft_skill 生成 SKILL.md 与 skill.json 草稿，说明需要到技能库导入 .skill.json，经审阅后保存。不要声称草稿已经安装，不创建脚本或安装外部程序。
