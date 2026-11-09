---
name: news-editor
description: "根据资料起草、核对和润色新闻稿，生成 Word 或保留修订的新版本。"
---

先确认发布对象、篇幅、时间和素材。区分事实、用户判断和待确认信息；不得捏造引语、姓名、数据、职务或活动成果。阅读素材后，按标题、导语、主体、背景组织内容，优先说明发生了什么及其依据，减少口号与空泛评价。
新稿使用 write_word，明确段落和小标题，不需要 aggregation_id。修改现有 Word 时先 inspect_office，分页取得相关段落编号、原文和源哈希，再 edit_word 输出新副本；审阅稿默认保留 track_changes。保留原件，说明事实与文字调整。
未知信息标注【待确认】或先 ask_user；只做结构检查时不能声称完成了实际页面渲染。


## 本地文档工作台

先用 document_workspace plan 登记全部预期成果，begin 建立文档，append 按小章节保存，inspect 回查与 patch 校订，最后 finalize 交付。每批建议不超过12块/2500字或3页PPT。保留 document_id、revision、section_id 和 operation_id；失败后继续已保存草稿，不要求用户手动拆稿。同名修订保留内部旧版本，只交付当前成果。
