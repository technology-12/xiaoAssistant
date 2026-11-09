# 第三轮：模型提供商、能力与上下文适配

日期：2026-11-04。候选入口：`artifacts/iteration3-candidate/WorkAssistant.Desktop.exe`。保留第二轮目录。对齐基线为本地 OpenCode v1.1.20，提交 `09ff3b9bb925903b36aa35fec25394133a42cf6d`；这是 C# 本地适配，不是运行完整上游内核。全量差距见 `OPENCODE_FULL_PARITY_MATRIX_2026-11-04.md`。

## 本轮落地

### 提供商协议

统一 `ModelClientFactory`，支持 `openai-chat` 与 `anthropic-messages`。主模型和子 Agent 模型可以使用不同协议、端点和密钥，本地 Host/Worker、权限与存储继续共用。

Messages 适配包括顶层 system、原生工具定义、tool_use/tool_result 配对、JSON 响应、命名 SSE 事件、增量 JSON 参数、完整结束检查、错误状态和缓存 token 用量。工具参数未完成或没有 message_stop 时不产生可执行调用。文本已展示后断流不重试；529 overload 使用有限重试。

协议依据：[Messages API](https://platform.claude.com/docs/en/api/messages/create)、[Streaming Messages](https://platform.claude.com/docs/en/build-with-claude/streaming)、[API version history](https://platform.claude.com/docs/en/api/versioning)。默认兼容版本 `2023-06-01`，可在高级设置修改；不发送 beta 功能配置。本轮不支持 thinking/签名块、图像等媒体或服务端工具；遇到未适配内容块明确返回 MODEL_FEATURE_UNSUPPORTED。

### 模型能力与缓存

使用合成 ping_probe 检查工具名称与 nonce，不用真实文件进行连接测试。确实返回有效工具调用才确认支持；单纯返回文字仍为未确认。

能力缓存按端点、模型、密钥、协议、版本、代理和请求超时绑定，有效期 24 小时。设置页主动测试会绕过旧的流式缓存。临时失败不覆盖既有确认结果；认证失败清除旧确认。

只有明确的 stream 不支持响应，或有效非流式结果，才确认不使用流式。普通 400 仍为未知；500 等临时错误即使正文含 unsupported 也不会缓存为不支持。认证状态优先于正文的上下文错误提示。单个客户端记住已确认的流式拒绝，避免每次调用重复发送同一失败模式。

### 上下文窗口与输出预留

设置页增加协议、上下文窗口、最大输出和 Messages API 版本。数值有范围及交叉校验，错误在字段组内显示并展开高级设置。主、子模型各自应用预算，预算事实存入 TaskRecord 并可回查。

有模型窗口时，先预留输出额度，再按约 85% 可用输入预算触发整理。参考上游 compaction/prune 机制，只收起本轮较早的工具结果投影，保留最近三组、完整工具调用配对及技能说明；原始步骤和消息不删除。随后按同一模型预算生成摘要，摘要请求也不能超窗。

收到明确的服务端上下文超限时，若没有展示部分文本且预算允许，会强制整理历史后进行一次受控重试。不能缩小、没有可压缩历史或再次超限时终止，不截断当前用户请求，不反复发送原请求。

**token 数量为保守估算，不是厂商 tokenizer 或计费值。** 估算包含消息和工具定义，对非 ASCII 文本采用更保守权重。未配置窗口的旧 Profile 保留 192K 字符整理阈值；请求仍受本机 512K 字符硬上限约束。未知模型窗口不会根据名称臆测。

上下文窗口允许 0 或 4096～2000000，最大输出允许 0～200000，显式输出必须小于窗口。0 表示自动：先读取工作区目录；窗口已知而输出未指定时预留 min(4096, 窗口/4)。未知窗口下 Chat Completions 不额外发送输出参数，Messages 默认 4096。

### 本地模型目录

工作区可创建 `.claude/opencode.models.json`，采用 OpenCode 风格的 provider/options/models/limit 结构：

```json
{
  "provider": {
    "office-gateway": {
      "options": { "baseURL": "http://192.0.2.10:8000/v1" },
      "models": {
        "office-model": { "limit": { "context": 32768, "output": 4096 } }
      }
    }
  }
}
```

仅在端点和模型匹配时填充未指定的限制；显式 Profile 值优先。端点主机名忽略大小写，路径区分大小写。支持模型条目 id 别名；重复匹配拒绝。目录在 Host 启动时读取，同一任务的主、子模型使用该次快照解析结果。

目录仅提供本地模型限制；不会导入其中的密钥、修改请求地址、加载 npm SDK 或执行代码，也不联网下载 models.dev。文件限制 256 KiB、JSON 深度 24，拒绝目录联接与重复 JSON 键。它不是完整 OpenCode 配置加载器。

## 代码交接

| 文件 | 职责 |
|---|---|
| Core/ModelContextBudget.cs | 输入估算、输出预留、旧工具结果投影及本地拒绝 |
| Core/ConversationContextManager.cs | 保留完整分组的预算内摘要 |
| Core/AgentController.cs | 主循环预算、单次溢出恢复、原始记录保留 |
| Infrastructure/ModelClientFactory.cs | 协议分派和输出限制 |
| Infrastructure/AnthropicMessageCodec.cs | Messages 结构和 SSE 状态机 |
| Infrastructure/AnthropicModelClient.cs | 认证、端点、读取、重试、取消 |
| Infrastructure/ModelConnectionProbe.cs | 合成能力探测 |
| Infrastructure/ModelCapabilityCache.cs | 指纹、有效期及失败语义 |
| Infrastructure/ModelServiceError.cs | 不保存正文的错误分类 |
| Infrastructure/WorkspaceModelCatalog.cs | 本地 OpenCode 风格限制目录 |
| Desktop/SettingsWindow.xaml(.cs) | 协议与预算表单、内联校验 |

上游对应入口：provider/models.ts、provider/provider.ts、provider/transform.ts、session/compaction.ts、util/token.ts。估算策略及 Win7 资源上限属于本项目的明确差异；模型目录和提供商 SDK 只适配了上述子集。

保留 ModelRequest 原四参数入口、原 CompactAsync 入口及旧结果/异常构造入口，避免扩展参数后破坏已有诊断或集成。诊断工程排除旧测试目录中的 DLL，并核对运行 DLL 哈希，确保编译和运行都面向候选版本。

## 验证与边界

Release 构建 0 警告、0 错误。本轮 50 项新增检查包含预算拒绝与压缩、原文保留、能力缓存/失效、错误分类、原生 Messages/SSE、跨协议主子 Agent、实际本地 Worker 读取，以及 WPF 设置页的控件校验和无窗口渲染。原有 138 项专项检查与 13 个 HTTP/SSE 兼容测试继续回归。

实际验证了 OpenAI 主 Agent 调度两个 Anthropic 子 Agent，分别读取各自文件；验证原生参数片段、tool_result 回传、模型目录限制传递和落盘。模型端均为本机模拟服务，使用合成凭据与材料，没有调用用户真实模型账号。

这不代表真实厂商、Win7 实机、长时间压力或 GUI 人工操作已经验收。对齐完整 OpenCode 还需推进推理元数据、更多协议、Agent/Skill 配置、代码编辑/终端、MCP/LSP 等，见全量矩阵。

复现：还原 artifacts/diagnostics/iteration3/Verify.csproj 后执行 `python artifacts/diagnostics/iteration3/http_integration.py`。其他诊断可通过 CandidatePath 或 XIAO_CANDIDATE 指定本轮目录。报告与设置页渲染图附在候选包 verification 中。
