# 晓助手 · OpenCode 内核迁移工程

本工程实际使用官方 OpenCode 1.1.20 完成 Agent 循环；保留 WPF/.NET 4.8 客户端和本地 Host/Worker 办公能力。旧构建保留在 `artifacts/agent-candidate` 等目录，可以回退。

## 本机启动（在现代 Windows 上运行服务端）

要求：Node.js 24、.NET Framework 4.8；Win7 只运行桌面端，不运行 Node/OpenCode。

先退出旧版晓助手及其托盘实例，在项目根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File migration/scripts/Start-Xiao.ps1
```

脚本生成本机服务令牌、加密客户端配置，隐藏启动网关，然后打开 `artifacts/opencode-candidate/WorkAssistant.Desktop.exe`。未安装服务依赖时使用 `npm ci` 按锁文件安装。脚本不会强制退出已运行的旧版。

若需要观察服务日志，可以先运行 `Initialize-Desktop.ps1`，再以前台方式运行 `Start-Gateway.ps1`；前台服务用 Ctrl+C 退出。不要同时启动两个相同端口的服务。

设置窗口新增“任务内核”区域：服务地址、访问令牌、检查内核连接，以及“使用 OpenCode 远程内核”开关。关闭开关回退本机内核。底部会显示当前内核类型。演示模式仍使用本地 Fake，不会访问网关或模型。

已有用户级 `opencode.connection.json` 优先于程序目录的引导配置；若此前手动保存过连接，请在设置窗口更新。引导配置使用 Windows 当前用户 DPAPI，不能直接转给另一台电脑/另一个用户使用。

## Win7 + 远程服务

1. 在现代 Windows 或 Linux 上部署 `gateway`，服务端可以访问 DeepSeek 或指定模型网关。
2. 为网关配置至少 32 字符随机令牌。Windows 启动脚本使用 `.local/gateway.token`；Linux 使用 `XIAO_GATEWAY_TOKEN_FILE` 指定文件。
3. 使用 HTTPS 反向代理，参考 `deploy/nginx.conf.example`。客户端要求非回环地址使用 HTTPS，保留证书验证。
4. Win7 客户端安装 .NET Framework 4.8，在设置中填写 HTTPS 网关地址和令牌，选择模型配置。
5. 首次使用该网关/模型配置时，桌面端会明确说明模型密钥与必要上下文将交给该服务；用户确认后才提交。

Linux 手动运行示例：

```sh
cd migration/gateway
npm ci --omit=dev
XIAO_GATEWAY_TOKEN_FILE=/secure/xiao.token XIAO_DATA_ROOT=/var/lib/xiao XIAO_HOST=127.0.0.1 npm start
```

也提供 Dockerfile 和 Compose。构建上下文为 migration，已用 `.dockerignore` 排除本地令牌、会话数据、上游源码及验证文件；Compose 仅将服务端口发布到回环地址，再由 HTTPS 代理转发。当前环境没有 Docker，容器路径未实际运行验证。

这是单用户/同信任域部署模型。一个令牌能够访问该网关的所有会话，尚无团队账号、租户隔离和配额管理；不要作为开放注册的多租户服务部署。

## 工程入口

| 位置 | 用途 |
|---|---|
| `../Xiao.OpenCode.sln` | .NET 客户端、适配层和本地执行器 |
| `../src/WorkAssistant.OpenCode` | Win7 兼容网关客户端与任务驱动 |
| `gateway/src/server.mjs` | 认证、任务生命周期、认领、回执及重启日志 |
| `gateway/src/opencode-runtime.mjs` | 固定版本 OpenCode 进程、会话、模型配置、SSE |
| `gateway/src/bridge-mcp.mjs` | OpenCode 到本地工具的 MCP stdio 桥 |
| `upstream/opencode` | 官方源码基线，保留原许可，不作自研代码统计 |
| `UPSTREAM.lock.json` | 标签、提交、许可证与可执行版本锁定信息 |
| `protocol/run.schema.json` | 桌面提交协议 |
| `verification` | 真实 OpenCode + 模拟模型 + .NET Worker 验证 |

细节与限制见项目 `docs/OPENCODE_KERNEL_MIGRATION.md`。规模见 `SOURCE_SCALE.md`，逐文件明细见 `SOURCE_SCALE.json`。

## 重现验证

```powershell
& C:/Users/YUAN/.dotnet/dotnet.exe build src/WorkAssistant.Desktop/WorkAssistant.Desktop.csproj -c Release --no-restore -m:1 -p:UseSharedCompilation=false -o artifacts/opencode-candidate
node --test migration/gateway/test/protocol.test.mjs
node migration/verification/integration.mjs
python migration/scripts/Count-MigrationSource.py
```

端到端验证只使用本机模拟模型和 `migration/.local/integration` 中的合成数据，不调用真实模型。专用诊断客户端也在该隔离目录构建，不属于分发文件。
