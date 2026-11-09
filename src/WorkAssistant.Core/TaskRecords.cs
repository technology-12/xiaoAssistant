using System;
using System.Collections.Generic;

namespace WorkAssistant.Core
{
    public enum TaskState
    {
        Created,
        Queued,
        Running,
        /// <summary>等待用户确认（如文件整理计划审批）；确认后继续运行。</summary>
        WaitingForUser,
        Completed,
        /// <summary>成果有效但存在告警（如最终解释请求失败）。</summary>
        CompletedWithWarnings,
        Failed,
        Cancelled,
        /// <summary>程序崩溃后遗留的 Running 任务，下次启动时标记。</summary>
        Interrupted,
        /// <summary>副作用处于不确定状态（文件整理部分完成），需人工复核。</summary>
        NeedsReview
    }

    /// <summary>Running 期间展示的子状态。</summary>
    public enum TaskSubState
    {
        None,
        RequestingModel,
        ExecutingTool,
        Exporting,
        /// <summary>等待用户审批（与 TaskState.WaitingForUser 对应）。</summary>
        WaitingForApproval
    }

    /// <summary>
    /// 任务类型（正式版 §5.5 完成证据）：不同类型以不同证据判定成功，
    /// 问答不强制生成文件；文件任务必须有通过校验的成果。
    /// </summary>
    public enum TaskKind
    {
        /// <summary>普通问答：完整响应即成功，不得宣称未执行的文件操作。</summary>
        QuestionAnswer,
        /// <summary>数据分析：以有效计算结果为证据。</summary>
        DataAnalysis,
        /// <summary>文件生成：成果可解析、路径合规、登记已提交。</summary>
        FileGeneration,
        /// <summary>文件修改：计划获批、变更记录完整、目标状态通过校验。</summary>
        FileModification
    }

    public enum TaskEventKind
    {
        TaskStarted,
        ModelRequestStarted,
        ModelRequestFinished,
        ToolCallStarted,
        ToolCallFinished,
        /// <summary>流式文本增量（仅对话内容展示；节流后落盘）。</summary>
        AssistantDelta,
        /// <summary>需要用户确认的操作请求（工具执行前的审批门）。</summary>
        ApprovalRequested,
        /// <summary>用户对审批请求的决定（approved/denied）。</summary>
        ApprovalResolved,
        TaskCompleted,
        TaskFailed,
        TaskCancelled,
        /// <summary>任务进入 NeedsReview（副作用不确定，不自动重试）。</summary>
        TaskNeedsReview,
        ContextCompactionStarted,
        ContextCompactionFinished,
        SubAgentStarted,
        SubAgentProgress,
        SubAgentFinished,
        ModelRetryScheduled,
        PermissionAutoApproved,
        ToolProgress
    }

    /// <summary>单个任务事件；event_id 为顺序 GUID，时间为 UTC。</summary>
    public class TaskEvent
    {
        public string EventId { get; }
        public DateTime UtcTime { get; }
        public string TaskId { get; }
        public TaskEventKind Kind { get; }
        public string? ToolName { get; }
        /// <summary>结果概要；脱敏后仅含可解释信息。</summary>
        public string Summary { get; }
        /// <summary>工具调用是否成功；仅 ToolCallFinished 事件有值，供界面结构化判断。</summary>
        public bool? ToolOk { get; }

        public TaskEvent(string eventId, DateTime utcTime, string taskId,
            TaskEventKind kind, string summary, string? toolName = null, bool? toolOk = null)
        {
            EventId = eventId;
            UtcTime = utcTime;
            TaskId = taskId;
            Kind = kind;
            Summary = summary;
            ToolName = toolName;
            ToolOk = toolOk;
        }
    }

    /// <summary>
    /// 会话（Session）与任务（Task）分离（正式版 §5.4）：
    /// 一个会话包含多轮任务；后续轮次可引用此前成果与口径；
    /// 每轮写入独立输出目录，不覆盖上一版。
    /// </summary>
    public class SessionRecord
    {
        public string ProjectId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string Title { get; set; } = "";
        public string WorkspaceRoot { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public DateTime LastActiveUtc { get; set; }
        public List<SessionTurn> Turns { get; set; } = new();

        public SessionRecord() { }

        public SessionRecord(string sessionId, string title, string workspaceRoot)
        {
            SessionId = sessionId;
            Title = title;
            WorkspaceRoot = workspaceRoot;
            CreatedUtc = DateTime.UtcNow;
            LastActiveUtc = CreatedUtc;
        }
    }

    /// <summary>会话中的一轮（对应一个 TaskRecord）。</summary>
    public class SessionTurn
    {
        public string TaskId { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public string Request { get; set; } = "";
    }

    /// <summary>任务主记录，持久化为 tasks/&lt;task_id&gt;/task.json。</summary>
    public class TaskRecord
    {
        public string ContextSummary { get; set; } = "";
        public int ContextCompactionCount { get; set; }
        public List<ChatMessage> ContextMessages { get; set; } = new();
        public string ParentTaskId { get; set; } = "";
        public string AgentName { get; set; } = "";
        public string AgentModelAlias { get; set; } = "";
        public string AgentRoleId { get; set; } = "general";
        public string ProjectConfigHash { get; set; } = "";
        public string ExpandedRequest { get; set; } = "";
        public string CommandName { get; set; } = "";
        public string AgentModelName { get; set; } = "";
        public List<string> ChildTaskIds { get; set; } = new();
        public List<string> DependencyTaskIds { get; set; } = new();
        public int SubAgentFailureCount { get; set; }
        public int ModelRetryCount { get; set; }
        public int ContextWindowTokens { get; set; }
        public int OutputReserveTokens { get; set; }
        public int EstimatedInputTokens { get; set; }
        public AgentExecutionMode ExecutionMode { get; set; } = AgentExecutionMode.Execute;
        public TaskPermissionMode PermissionMode { get; set; } = TaskPermissionMode.Assisted;
        public string TaskId { get; set; }
        public TaskState State { get; set; }
        public TaskSubState SubState { get; set; }
        /// <summary>任务类型：完成证据判定与提示词组装依据。</summary>
        public TaskKind Kind { get; set; }
        public string UserRequest { get; set; } = "";
        public string? ErrorCode { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public List<ArtifactInfo> Artifacts { get; set; } = new();
        public List<ChatMessage> Messages { get; set; } = new();
        public string WorkspaceRoot { get; set; } = "";
        public string FinalText { get; set; } = "";
        /// <summary>所属会话；单轮独立任务也有会话（仅一轮）。</summary>
        public string SessionId { get; set; } = "";
        /// <summary>会话内轮次序号（从 1 开始）。</summary>
        public int TurnIndex { get; set; }
        /// <summary>本任务是否产生了需要用户确认的操作（审批门触发过）。</summary>
        public bool ApprovalRequested { get; set; }
        /// <summary>模型用量（服务端返回时累计；缺失时保持 0，界面显示“未知”）。</summary>
        public long PromptTokens { get; set; }
        public long CompletionTokens { get; set; }
        public bool HasCalculationEvidence { get; set; }
        public bool HasMutationEvidence { get; set; }
        public string? SnapshotRoot { get; set; }
        public string SkillId { get; set; } = "";
        public string SkillVersion { get; set; } = "";
        public string SkillSelectionSource { get; set; } = "";
        public List<StepRecord> Steps { get; set; } = new();

        public TaskRecord(string taskId, string userRequest, string workspaceRoot)
        {
            TaskId = taskId;
            State = TaskState.Created;
            SubState = TaskSubState.None;
            Kind = TaskKind.QuestionAnswer;
            UserRequest = userRequest;
            WorkspaceRoot = workspaceRoot;
            CreatedUtc = DateTime.UtcNow;
        }
    }

    public sealed class StepRecord
    {
        public string CallId { get; set; } = "";
        public string Tool { get; set; } = "";
        public string ToolVersion { get; set; } = "1";
        public bool WritesFiles { get; set; }
        public string ArgumentsJson { get; set; } = "";
        public string State { get; set; } = "Started";
        public string? ResultJson { get; set; }
        public string? CommitManifestPath { get; set; }
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>工具调用回执：key 为 (task_id, tool_call_id)。</summary>
    public class ToolReceipt
    {
        public string ToolCallId { get; }
        public string ToolName { get; }
        public string ArgumentsJson { get; }
        public bool Ok { get; }
        /// <summary>回执内容 = 返回给模型的工具结果 JSON（已脱敏）。</summary>
        public string ResultJson { get; }

        public ToolReceipt(string toolCallId, string toolName, string argumentsJson,
            bool ok, string resultJson)
        {
            ToolCallId = toolCallId;
            ToolName = toolName;
            ArgumentsJson = argumentsJson;
            Ok = ok;
            ResultJson = resultJson;
        }
    }
}
