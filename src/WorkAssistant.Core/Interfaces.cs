using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WorkAssistant.Core
{
    /// <summary>模型客户端抽象：非流式 Chat Completions。真实实现为 HTTP，替身为 Fake。</summary>
    public interface IModelClient
    {
        Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken);
    }

    /// <summary>
    /// 流式模型客户端（正式版 §7.2）：
    /// - SSE 增量回调；工具参数片段按调用索引缓存，结束信号后才解析执行。
    /// - 网络断流抛 MODEL_UNAVAILABLE，不把部分文字当成功。
    /// </summary>
    public interface IStreamingModelClient : IModelClient
    {
        Task<ModelReply> CompleteStreamAsync(ModelRequest request,
            Action<string>? onTextDelta, CancellationToken cancellationToken);
    }

    /// <summary>可被模型调用的工具。</summary>
    public interface IAgentTool
    {
        ToolDefinition Definition { get; }
        Task<ToolResult> ExecuteAsync(ToolContext context, string argumentsJson,
            CancellationToken cancellationToken);
    }

    /// <summary>工作区路径策略：相对路径 <-> 物理路径，边界校验在此集中完成。</summary>
    public interface IWorkspacePolicy
    {
        /// <summary>返回工作区根目录（宿主内部使用；不暴露给模型）。</summary>
        string RootPath { get; }
        /// <summary>解析可读路径（仅 inputs 及其子目录）。越界抛 ToolException(PATH_DENIED)。</summary>
        string ResolveReadablePath(string relativePath);
        /// <summary>解析任务输出路径（仅 outputs/&lt;taskId&gt;）。越界抛 ToolException(PATH_DENIED)。</summary>
        string ResolveTaskOutputPath(string taskId, string fileName);
        /// <summary>物理路径转工作区相对路径（'/' 分隔）。</summary>
        string ToWorkspaceRelative(string fullPath);
    }

    /// <summary>任务事件落地接口。</summary>
    public interface ITaskEventSink
    {
        void Append(TaskEvent taskEvent);
    }
    public interface ITaskCheckpointSink
    {
        void SaveCheckpoint(TaskRecord task);
    }

    // ---- 审批门（正式版 §5.3/§9.2：授权绑定具体操作，一次确认不扩展为未来授权）----

    /// <summary>需要用户确认的操作请求；展示具体文件与动作，不笼统授权。</summary>
    public sealed class ApprovalRequest
    {
        public string RequestId { get; }
        public string Title { get; }
        /// <summary>操作明细（每项一条，如“移动 inputs/a.pdf → inputs/已归档/a.pdf”）。</summary>
        public IList<string> Operations { get; }
        /// <summary>为什么需要确认的简短说明。</summary>
        public string Reason { get; }
        public string ToolName { get; }
        [Newtonsoft.Json.JsonIgnore] public TextEditApprovalBinding? TextEditBinding { get; set; }

        public ApprovalRequest(string toolName, string title,
            IList<string> operations, string reason, string? requestId = null)
        {
            ToolName = toolName;
            RequestId = requestId ?? Guid.NewGuid().ToString("N");
            Title = title;
            Operations = operations;
            Reason = reason;
        }
    }

    /// <summary>
    /// 审批提供者：由宿主实现（桌面弹窗、演示模式自动通过、测试注入固定结果）。
    /// 返回 true = 用户明确同意本次操作；false 或异常 = 拒绝（fail-closed）。
    /// </summary>
    public interface IApprovalProvider
    {
        Task<bool> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken);
    }

    /// <summary>自动批准（演示/自测用）。真实任务由桌面端提供弹窗实现。</summary>
    public sealed class AutoApproveProvider : IApprovalProvider
    {
        public Task<bool> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
            => XpTask.FromResult(true);
    }

    /// <summary>一律拒绝（fail-closed 默认实现：未配置审批提供者时不执行敏感操作）。</summary>
    public sealed class DenyAllProvider : IApprovalProvider
    {
        public Task<bool> RequestAsync(ApprovalRequest request, CancellationToken cancellationToken)
            => XpTask.FromResult(false);
    }

    // ---- 文件整理计划（正式版 §8.4：计划 → 预检 → 提交 → 校验）----

    /// <summary>计划中的单个文件操作。1.0 只允许 move（移动/改名），不允许删除。</summary>
    public sealed class FilePlanOperation
    {
        public string Source { get; }
        /// <summary>目标工作区相对路径（inputs/ 之下）。</summary>
        public string Target { get; }
        public string SourceSha256 { get; }

        public FilePlanOperation(string source, string target, string sourceSha256 = "")
        {
            Source = source;
            Target = target;
            SourceSha256 = sourceSha256;
        }
    }

    /// <summary>文件整理计划；plan_id 仅在本任务生命周期内有效（同 aggregation_id 规则）。</summary>
    public sealed class FilePlanRecord
    {
        public string PlanId { get; }
        public IList<FilePlanOperation> Operations { get; }
        /// <summary>planned / applied / partial。</summary>
        public string Status { get; set; }
        public TextEditProposal? TextEdit { get; set; }
        public string TextEditApplyReceiptSha256 { get; set; } = "";
        public string TextEditRollbackReceiptSha256 { get; set; } = "";
        /// <summary>已执行的操作（apply 阶段逐项追加，用于部分完成恢复）。</summary>
        public List<string> AppliedSources { get; } = new();

        public FilePlanRecord(string planId, IList<FilePlanOperation> operations)
        {
            PlanId = planId;
            Operations = operations;
            Status = "planned";
        }
    }

    /// <summary>文件整理计划的任务内暂存。</summary>
    public interface IFilePlanStore
    {
        string Add(FilePlanRecord plan);
        FilePlanRecord? Find(string planId);
    }
    public interface IPersistentPlanStore : IFilePlanStore
    {
        void Save(FilePlanRecord plan);
    }
}
