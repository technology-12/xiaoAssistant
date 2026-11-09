---
name: github-pptx
description: "从 MIT PPT 技能适配的默认流程：规划页面、生成可编辑课件和讲稿、修改现有普通文本并回读校对。"
---

改编自 darce07/pptx-mit-claude-skill（MIT），保留“规划 → 创建或精确编辑 → 校读 → 说明验收范围”的方法；Python 与渲染脚本调用改为晓助手现有工具，不安装依赖、不发送诊断到外部服务。

先明确听众、用途、时间、页数、语言和素材。遵循用户结构；每页一个要点，标题表达结论时不能添加材料中没有的结论。说明细节放 notes，数字与引语有来源；未知信息保留待确认。

新稿：write_presentation，slides 每项为 {title,layout:"cover"或"content",bullets:[文本],notes:"讲稿"}。文字太密被拒绝时拆页或征求内容取舍，不偷偷截断或把字体缩到不可读。创建步骤与参数示例见 references/workflow.md。

原稿：先 inspect_office(path=file_id)，按实际 slide_number/shape_id/paragraph_index、expected_text 和 source_sha256 调用 edit_presentation，输出新副本。默认保留未修改部件，不重建用户的整份演示文稿以覆盖个人修改。

生成或修改后，用 inspect_office(artifact_name=输出文件名) 分页回读正文，查看 proofread_findings，检查遗漏、乱码、重复词和待确认数据。当前回读不含讲者备注，讲稿还须对照已提交的 notes 逐页核对；不能声称从成品验证过未返回的部分。

质量细节使用 read_skill(id="github-pptx", resource="references/quality.md")。若用户要求检查或优化既有 PPT，可再阅读 github-pptx-review；它不扩张当前工具清单。

当前生成器只有统一的文字版式及讲者备注，不支持上游原生图表、图片、动画、主题任意重排或 COM 操作。不能把整页截图冒充原生可编辑对象。render_verified=false 时，明确尚未实际渲染验收；文本检查通过不等于页面无溢出。

来源、提交与改动见 source.json；MIT 声明见 LICENSE.txt。


## 本地文档工作台

先用 document_workspace plan 登记全部预期成果，begin 建立文档，append 按小章节保存，inspect 回查与 patch 校订，最后 finalize 交付。每批建议不超过12块/2500字或3页PPT。保留 document_id、revision、section_id 和 operation_id；失败后继续已保存草稿，不要求用户手动拆稿。同名修订保留内部旧版本，只交付当前成果。
