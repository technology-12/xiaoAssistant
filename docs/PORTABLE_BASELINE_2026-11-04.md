# 晓助手第七轮基线便携包

分发文件：`artifacts/packages/XiaoAssistant-1.0.0-baseline7-20261104-portable.zip`，126950063 字节，约 121 MiB。应用程序集直接冻结自已通过 426 项检查的 `iteration7-skills-candidate`，没有重新编译内核或改动业务行为，原 release 目录保留。

## 换机使用

将整个 ZIP 复制到另一台 Windows 电脑并完整解压，双击根目录 `Start-Xiao.cmd`。已有 .NET Framework 4.8 或兼容更新版本时直接启动；缺少时打开包内微软官方离线运行库的交互式安装器，按提示安装、必要时重启后再启动。系统运行库可能需要管理员权限，应用本身无需安装或提权。

不需要 Visual Studio、.NET SDK、Python、Node、Codex、Claude Code 或远程 OpenCode 内核。文档工具通过本地 OOXML 处理 Word/PPT；实际查看文件页面需要关联的兼容办公软件。模型使用目标电脑能访问的 API。

用户配置及历史继续存放在 `%LOCALAPPDATA%/Win7WorkAssistant`，工作区由用户在新电脑重新选择。本次没有改成随 U 盘漫游的数据模式，也没有复制开发电脑的历史、数据库、工作区或 DPAPI 加密凭据。按用户已有要求保留软件随附的默认模型配置。

## 包内容

- Start-Xiao.cmd：关键组件与 .NET 版本检查、运行库安装引导和正常用户权限启动。
- Check-Environment.cmd：只做基本前置检查，不启动安装器或访问模型。
- app：Desktop、Host、托管 DLL、x86/x64 SQLite、图标、配置、11 个技能、样例、许可证、说明与基线验证记录。
- prerequisites：微软官方 .NET Framework 4.8 x86/x64 离线运行库与来源记录，原文件 SHA256 为 `0a3a390c47e639d0f7fc65b21195fee6b7f65b066f80f70c60fab191d14b7e40`；打包时 Microsoft Corporation Authenticode 签名有效。
- README.txt、baseline.json、SHA256SUMS.txt：启动说明、基线信息和包内文件校验值。

运行库来源：[微软官方下载页](https://dotnet.microsoft.com/en-us/download/dotnet-framework/thank-you/net48-offline-installer)。应用本身是未签名便携构建，没有使用旧签名安装器冒充签名发行版。

## 打包验收

包内 172 个文件；ZIP CRC 与清单中 171 个文件的 SHA256 全部核对。SHA256SUMS 自身不自包含，其完整性由外置 ZIP SHA256 绑定。

重新解压到含空格的独立目录，从与应用无关的工作目录启动测试探针；实际 x64、x86 进程分别通过 10 项检查，包括全新隔离设置的默认模型加载、SQLite 持久化、11 个内置技能及参考资料、Word/PPT 生成、成果回读、Host IPC、隔离 Worker 和无窗口 WPF 渲染。测试不调用真实模型，不改写现有用户 AppData。

完整包前置检查成功；临时移除解压副本中的 SQLite 组件时，启动脚本以错误码 4 拒绝启动，恢复文件后检查成功。测试只修改解压验证副本，分发 ZIP 未改动。

SQLite 两种本机 DLL 的直接导入仅包含 Windows/CLR 系统库，没有发现独立 Visual C++ 运行库依赖。

原有基线检查为 426 项；本次新增的 20 项换路径运行检查及启动器检查单独记录，不冒充重新执行全部基线回归。实际 Win7 实机、全新缺运行库系统的安装过程及真实模型服务仍待目标环境验收；老旧 Win7 缺失的系统更新、证书、TLS 等不由便携包自动补齐。

外置校验记录：同目录 `.zip.sha256` 与 `.validation.json`；构建脚本 `scripts/Build-PortableBaseline.py`。脚本拒绝覆盖同名冻结包，后续改动须使用新包名称。
