---
name: github-pptx-review
description: "从 MIT PowerPoint 质量技能适配：按页核对标题、信息密度、事实与编辑范围，输出问题或修改副本。"
---

改编自 PeterKalmstrom/claude-powerpoint-skill 的 building-powerpoint-decks（MIT）。本迁移保留审阅、先快照后改、尊重用户修改、讲稿承载细节及显式质量反馈的方法，不宣称具备上游渲染器、图表、素材、直播演示或自动修复脚本。

先确认用户要审阅报告、保留设计的文字优化，还是允许重组为新稿。已有设计与人工修改优先；不默认重建所有页面或删除原始文件。

1. inspect_office(path=file_id) 分页阅读实际页面、形状和文字，记录 source_sha256。只返回部分正文时继续分页，不凭单页断言整份课件完成审阅。
2. 按页列出问题：标题是否有明确含义、正文是否支持标题、信息是否重复或过密、单位/来源是否一致、是否含待确认事实。问题定位到页码和形状，区分确定问题、疑点和无法检查的项目。
3. 只审阅时输出建议即可。允许文字优化时，以准确 expected_text 和定位调用 edit_presentation 输出新副本；容量错误时缩短经用户同意的措辞或说明需重新排版，不悄悄删事实。
4. 用 inspect_office(artifact_name=修改后的文件名) 回读，核对修改和遗漏。发现疑点再改时使用新版本名，不假装可以跨任务写任意成果路径。
5. 交付问题清单或新副本、修改清单，并说明未执行的视觉检查。详情使用 read_skill(id="github-pptx-review", resource="references/audit.md")。

render_verified=false 时不能给出“所有页面无溢出、图表无误、字体完全一致”的结论。自动审阅不核实真实世界事实；引用和数字只能依据用户材料。当前不发送任何错误报告，不自动上传文档，也不接管用户已打开的 Office 窗口。

来源、提交和改动见 source.json；MIT 声明见 LICENSE.txt。


## 本地文档工作台

先用 document_workspace plan 登记全部预期成果，begin 建立文档，append 按小章节保存，inspect 回查与 patch 校订，最后 finalize 交付。每批建议不超过12块/2500字或3页PPT。保留 document_id、revision、section_id 和 operation_id；失败后继续已保存草稿，不要求用户手动拆稿。同名修订保留内部旧版本，只交付当前成果。
