# 第四轮项目配置示例

把本目录中的 AGENTS.md 和 .claude 复制到需要启用这些规则的工作区根目录；已有同名文件时先比较内容。示例不修改输入文件、不导入任何模型密钥。

输入 `/audit 检查材料日期和数字是否一致`，或 `/agent auditor 核对输入文件`。也可以在技能工作室选择 evidence。主 Agent 可使用 `subagent_type: auditor` 派发只读审查任务。

模型别名 worker 引用设置里的子 Agent 模型，未单独配置时跟随主模型。移植范围和语法限制详见 docs/OPENCODE_ADAPTATION_ITERATION4_2026-11-04.md。

第五轮：可把 inputs/text-edit-demo.txt 复制进工作区 inputs，再输入 `/propose-edit 把 text-edit-demo.txt 中的“待核对”改为“已核对”`。只会生成差异与候选文件，原件不变；正式审批提交留在第六轮实现。

第六轮：输入 `/apply-edit 把 text-edit-demo.txt 中的“待核对”改为“已核对”`，模型生成提案后会请求本次确认，再实际提交。需要当前卷支持 NTFS 事务；不可用时拒绝提交，不普通覆盖。回滚也需单独确认，且不能覆盖后续更新。详见 docs/OPENCODE_ADAPTATION_ITERATION6_2026-11-04.md。
