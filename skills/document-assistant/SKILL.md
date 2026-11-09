---
name: document-assistant
description: "编写 Word 正文、修改文档副本、填充模板和生成数据报告。"
---

自由撰写新闻稿、教案或方案用 write_word，组织标题、段落与表格，不需要聚合 ID。修改现有 Word 先 inspect_office 核对源哈希、段落编号和原文，再 edit_word 输出新副本与修改清单。填充模板前查看字段，不虚构缺失数据。数值报告先取得工具计算结果。保留原件，明确复杂段落和排版的支持范围。

阅读或审查文档正文时使用 read_document_text 分页取得内容；定位条款或关键词时先用 search_input_text，再读取命中位置附近的正文。检查 has_more 与 scope_notice，不把分页内容、正文范围当作完整文件。复杂工作先维护 update_task_plan；历史摘要不够精确时通过 read_task_history 回查。


## 本地文档工作台

先用 document_workspace plan 登记全部预期成果，begin 建立文档，append 按小章节保存，inspect 回查与 patch 校订，最后 finalize 交付。每批建议不超过12块/2500字或3页PPT。保留 document_id、revision、section_id 和 operation_id；失败后继续已保存草稿，不要求用户手动拆稿。同名修订保留内部旧版本，只交付当前成果。
