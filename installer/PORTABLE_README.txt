晓助手 · 第七轮基线便携包（含 GitHub 办公技能）
基线日期：2026-11-04

一、在另一台电脑上使用
1. 将整个 ZIP 复制到目标电脑，右键选择“全部解压”，例如解压到 D:\XiaoAssistant。
2. 双击包根目录的 Start-Xiao.cmd。
3. 电脑已有 .NET Framework 4.8 或更高兼容版本时，直接启动晓助手。
4. 如果缺少运行库，启动脚本会打开 prerequisites 中的微软官方离线安装器。
   运行库安装可能需要管理员权限。若要求重启，请重启后再次双击 Start-Xiao.cmd。
5. 创建或选择目标电脑上的工作区，添加文件后即可使用。右上角可以切换模型。

应用本身无需安装。请保留 app 文件夹及其全部内容，不能只复制一个 EXE。
已安装运行库时，也可直接打开 app\WorkAssistant.Desktop.exe。

二、环境要求与范围
- Windows 7 SP1 / Windows 10 / Windows 11 桌面系统；应用为 .NET Framework 4.8 WPF。
- 包含 SQLite x86/x64 本机组件和应用需要的其他 DLL。
- 不需要安装 Visual Studio、.NET SDK、Python、Node、Codex 或 Claude Code。
- 生成、读取和编辑 docx/pptx 不要求安装 Microsoft Office。打开文件进行实际页面
  查看时，需要目标电脑关联的兼容办公软件。
- 模型计算需要目标电脑能访问已配置的模型 API 或指定服务器。
- 很旧的 Win7 系统可能还缺少微软系统更新、证书或 TLS 相关组件；这些属于系统
  维护范围，本包不自动修改。离线运行库安装失败时请先查看安装器提示。
- 本包没有在另一台 Win7 实机上完成验收，不能视为对所有精简系统的兼容承诺。
- 当前基线为未签名便携构建；如系统或组织策略阻止执行，请由管理员按策略处理。

三、配置与数据
默认模型配置保留在 app\app.defaults.json，与本次已验证基线一致。
在全新电脑上会创建该电脑自己的用户配置；默认模型可用性取决于网络、服务和额度。
已有旧版用户配置时，沿用或迁移规则由软件原有设置逻辑处理，可在设置中检查。

本包不携带开发电脑的聊天记录、个人工作区、本地数据库和用户加密凭据。
实际用户数据写入：%LOCALAPPDATA%\Win7WorkAssistant
工作区文件写入用户自己选择的工作区。
因此，这是“免安装分发包”，用户数据不随 U 盘自动漫游。
迁移已有工作内容需要单独复制工作区，并在目标电脑重新选择路径；不要直接复制
旧电脑 DPAPI 加密的 credentials，目标电脑应重新配置个人 API Key。
删除解压目录只移除程序，不会自动删除用户数据或工作区。

四、默认能力
包含当前基线的 11 个内置技能，其中新增 Word 文档编辑、PPT 编写与编辑、
PPT 审阅与优化三项 GitHub 迁移技能（MIT 许可）。未手选技能时自动匹配 Word/PPT
任务，明确手选优先。全部技能和许可证随包提供。
支持范围及尚未实现的图片、复杂图表、自动页面渲染等见 app\docs 中的迁移说明。

五、检查与故障排查
- 双击 Check-Environment.cmd：只检查关键文件和 .NET 版本，不访问模型、不安装系统组件。
- 程序启动不了：确认已完整解压；运行库已安装；未单独移动或隔离 Host/DLL 文件。
- WORKER_UNAVAILABLE：检查 app\WorkAssistant.Host.exe 和 x86/x64 组件是否完整，
  再查看杀毒软件记录与软件诊断信息；不要直接关闭全部防护。
- 模型认证失败：在设置中测试连接，并检查 API 地址、模型名称、Key、余额与代理。
- 不要从 ZIP 预览窗口中直接运行程序，也不要把工作区放在临时解压目录。

app\verification 保存冻结基线的 426 项检查记录与程序集哈希。
baseline.json 记录本次包信息；SHA256SUMS.txt 记录包内文件哈希。
微软运行库来源与签名信息见 prerequisites\source.json。

微软官方运行库页面：
https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48
