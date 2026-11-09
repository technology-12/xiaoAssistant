using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Core
{
    /// <summary>对话消息。覆盖 system/user/assistant/tool 四种角色与原生工具调用。</summary>
    public class ChatMessage
    {
        public string Role { get; }
        public string? Content { get; }
        /// <summary>assistant 消息携带的工具调用列表；其他角色为 null。</summary>
        public IList<ToolCall>? ToolCalls { get; }
        /// <summary>role=tool 时必须携带，与请求侧 tool_calls[].id 匹配。</summary>
        public string? ToolCallId { get; }
        public string? ReasoningContent { get; }

        public ChatMessage(string role, string? content, IList<ToolCall>? toolCalls, string? toolCallId)
            : this(role, content, toolCalls, toolCallId, null) { }

        [Newtonsoft.Json.JsonConstructor]
        public ChatMessage(string role, string? content,
            IList<ToolCall>? toolCalls = null, string? toolCallId = null, string? reasoningContent = null)
        {
            Role = role;
            Content = content;
            ToolCalls = toolCalls;
            ToolCallId = toolCallId;
            ReasoningContent = reasoningContent;
        }

        public static ChatMessage System(string content) => new("system", content);
        public static ChatMessage User(string content) => new("user", content);
    }

    /// <summary>一次函数调用（id + 函数名 + 参数 JSON 字符串）。</summary>
    public class ToolCall
    {
        public string Id { get; }
        public string Name { get; }
        /// <summary>原始 arguments JSON 字符串；由工具端解析校验，不做隐式修复。</summary>
        public string ArgumentsJson { get; }

        public ToolCall(string id, string name, string argumentsJson)
        {
            Id = id;
            Name = name;
            ArgumentsJson = argumentsJson;
        }
    }

    /// <summary>Chat Completions 请求（非流式与流式共用消息结构）。</summary>
    public class ModelRequest
    {
        public string Model { get; }
        public IList<ChatMessage> Messages { get; }
        public IList<ToolDefinition> Tools { get; }
        public string ToolChoice { get; }
        public int MaxOutputTokens { get; }

        public ModelRequest(string model, IList<ChatMessage> messages,
            IList<ToolDefinition> tools, string toolChoice = "auto") : this(model, messages, tools, toolChoice, 0) { }
        public ModelRequest(string model, IList<ChatMessage> messages,
            IList<ToolDefinition> tools, int maxOutputTokens) : this(model, messages, tools, "auto", maxOutputTokens) { }
        public ModelRequest(string model, IList<ChatMessage> messages,
            IList<ToolDefinition> tools, string toolChoice, int maxOutputTokens)
        {
            Model = model;
            Messages = messages;
            Tools = tools;
            ToolChoice = toolChoice;
            MaxOutputTokens = maxOutputTokens;
        }
    }

    /// <summary>服务端返回的用量（可能缺失；缺失时界面显示“未知”）。</summary>
    public class ModelUsage
    {
        public long PromptTokens { get; }
        public long CompletionTokens { get; }
        public long ReasoningTokens { get; }

        public ModelUsage(long promptTokens, long completionTokens) : this(promptTokens, completionTokens, 0) { }

        [Newtonsoft.Json.JsonConstructor]
        public ModelUsage(long promptTokens, long completionTokens, long reasoningTokens = 0)
        {
            PromptTokens = promptTokens;
            CompletionTokens = completionTokens;
            ReasoningTokens = reasoningTokens;
        }
    }

    /// <summary>一次模型响应：完整 assistant 消息（含 tool_calls）与 finish_reason。</summary>
    public class ModelReply
    {
        public string? Content { get; }
        public IList<ToolCall>? ToolCalls { get; }
        public string FinishReason { get; }
        public string? ReasoningContent { get; }
        /// <summary>本次响应是否来自流式通道（用于诊断展示）。</summary>
        public bool Streamed { get; }
        /// <summary>服务端用量；无 usage 字段时为 null。</summary>
        public ModelUsage? Usage { get; }

        public ModelReply(string? content, IList<ToolCall>? toolCalls, string finishReason, bool streamed, ModelUsage? usage)
            : this(content, toolCalls, finishReason, streamed, usage, null) { }

        [Newtonsoft.Json.JsonConstructor]
        public ModelReply(string? content, IList<ToolCall>? toolCalls, string finishReason,
            bool streamed = false, ModelUsage? usage = null, string? reasoningContent = null)
        {
            Content = content;
            ToolCalls = toolCalls;
            FinishReason = finishReason;
            Streamed = streamed;
            Usage = usage;
            ReasoningContent = reasoningContent;
        }

        public bool HasToolCalls => ToolCalls is { Count: > 0 };
    }

    /// <summary>
    /// 注册到模型的工具定义；parameters 为 JSON Schema。
    /// 元数据（正式版 §8.1）：
    /// - ProducesArtifacts：该工具产出需登记校验的文件成果（完成证据来源）。
    /// - RequiresApproval：执行前必须经过用户确认（审批门 → WaitingForUser）。
    /// </summary>
    public class ToolDefinition
    {
        public string Name { get; }
        public string Description { get; }
        public JObject Parameters { get; }
        public bool ProducesArtifacts { get; }
        public bool RequiresApproval { get; }
        public bool PolicyRequiresApproval { get; set; }
        public string Version { get; } = "1";
        public string Risk => RequiresApproval ? "modify-inputs" : ProducesArtifacts ? "write-output" : "read-only";
        public int TimeoutSeconds => Name == "read_document_text" || Name == "extract_pdf_text" || Name == "search_input_text" ? 600 : 60;
        public string Recovery => RequiresApproval ? "manual-reconcile" : "replay-from-snapshot";
        public bool IsCalculation => Name == "aggregate_csv" || Name == "aggregate_table";
        public bool IsMutation => Name == "apply_file_plan" || ApprovedToolOperation.IsTextMutation(Name);
        public bool IsPlan => Name == "plan_file_organization" || Name == "plan_text_edit";
        public string[] Permissions => RequiresApproval ? new[] { "read-input-snapshot", "modify-approved-inputs", "write-task-output" } :
            ProducesArtifacts ? new[] { "read-input-snapshot", "write-task-output" } : new[] { "read-input-snapshot" };
        public long MaximumInputBytes => 100L * 1024 * 1024;
        public int MaximumContextResultChars => AgentController.MaxToolResultChars;
        public JObject OutputSchema => JObject.Parse(@"{""type"":""object"",""required"":[""ok"",""data"",""artifacts""],""properties"":{""ok"":{""type"":""boolean""},""data"":{""type"":""object""},""artifacts"":{""type"":""array""},""error"":{""type"":""object""}}}");

        public ToolDefinition(string name, string description, JObject parameters,
            bool producesArtifacts = false, bool requiresApproval = false)
        {
            Name = name;
            Description = description;
            Parameters = parameters;
            ProducesArtifacts = producesArtifacts;
            RequiresApproval = requiresApproval;
        }
    }
}
