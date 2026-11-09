---
name: presentation-author
description: "把资料组织成逐页演示文稿、讲者备注，并修改现有 PPT 普通文本。"
---

确认听众、目的、时间和页数。每页围绕一个信息点，标题简短，正文不超过六点，详细解释放入 notes。数字与案例标注依据，不编造图表。
write_presentation 输出真正可编辑的 PPTX；文字太密被拒绝时拆页，不截断内容。当前是统一文字版式，不宣称已创建图表、图片或复杂动画。
修改现有 PPT 先 inspect_office，按实际页面、形状、段落编号与原文调用 edit_presentation。保留原件、未修改部件，新文字继承首个文字片段样式。复杂占位符、图表、SmartArt 需人工检查。文字预览和结构校验不等于实际幻灯片渲染。


## 本地文档工作台

先用 document_workspace plan 登记全部预期成果，begin 建立文档，append 按小章节保存，inspect 回查与 patch 校订，最后 finalize 交付。每批建议不超过12块/2500字或3页PPT。保留 document_id、revision、section_id 和 operation_id；失败后继续已保存草稿，不要求用户手动拆稿。同名修订保留内部旧版本，只交付当前成果。
