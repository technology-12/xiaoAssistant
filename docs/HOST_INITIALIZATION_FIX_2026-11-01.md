# 任务初始化 ArgumentException 修复

现象：普通任务在输入快照创建后、首个任务事件之前退出，界面显示 WORKER_UNAVAILABLE。读取现有本地任务状态确认异常类型为 ArgumentException，步骤数为 0。

根因：`FileReferenceTool` 遍历工具参数 schema 中名为 description 的所有属性，将它们无条件转换为字符串。新增 `draft_skill` 工具本身有一个参数也叫 description，该属性的值是参数定义对象，转换触发 `Can not convert Object to String`。普通助手初始化全部内置工具，因此同样受到影响。

修复：仅处理值类型为 String 的 description 元数据，替换前将枚举结果固定为列表，保留名为 description 的参数对象。

离线定位记录：`artifacts/diagnostics/schema-repro/inspect.ps1`。该诊断只创建工具和包装器，不启动任务、不连接模型、不修改原工作区。修复前前 14 个工具正常、draft_skill 构造失败；修复后 15 个工具包装器均可构造。未运行完整测试套件或端到端模型任务。

同时改善诊断：宿主错误帧返回错误码、执行阶段、异常类型及有限的方法名列表；不保存通用异常正文、参数值、源代码文件路径、任务正文或凭据。任务“执行详情”可查看这些记录。领域错误保留适用的错误码，通用初始化/运行异常分别标记 HOST_INITIALIZATION_FAILED / HOST_EXECUTION_FAILED；真正的进程或管道中断仍为 WORKER_UNAVAILABLE。已有失败任务保留原历史记录，不自动重新执行。

交付后应完全退出旧程序（包含托盘）并重新启动完整候选目录内的晓助手。旧进程已经载入的程序集不会因磁盘文件重建而自动刷新。重试会创建新的任务，不覆盖既有成果。
