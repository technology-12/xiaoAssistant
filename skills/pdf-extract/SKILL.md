---
name: pdf-extract
description: "读取 PDF 文字，扫描页自动本地 OCR，保留页码、识别提示与处理范围。"
---

根据用户范围选择 PDF 和页数，使用 extract_pdf_text，用户需要文件时设置 write_file=true。引用内容附页码，说明是否仅处理部分页面。扫描页与较大图片混合页会自动进行本地中文/英文 OCR；以工具返回的 ocr_pages、low_confidence_pages 和 scope_notice 为准，不把旧对话中的“未启用 OCR”当作当前能力判断。首次识别可能需要数分钟，软件显示进度；后续分页复用本任务的缓存。

OCR 可能有错字、漏字和阅读顺序偏差，复杂表格或公式不能当作已准确还原。图像层回退会单列原有文字层，注意重复或顺序；低置信度页要提示核对。没有识别出文字时说明具体失败范围，不能根据文件名或无关文档编造原文。不得在未获授权时改用远程 OCR 或上传整份文档。

需要连续阅读或检索时，使用 read_document_text 和 search_input_text，保留返回内容中的页码标记。只读分析模式不导出文件。检查 partial_document 和 has_more；不能把前几页预览作为全文结论。
