using System.Collections.Generic;

namespace WorkAssistant.Core
{
    /// <summary>
    /// 工具执行结果。ok/data/error/artifacts 四要素：
    /// error 仅含稳定错误码与可读消息；artifacts 为本次调用新登记的成果。
    /// </summary>
    public class ToolResult
    {
        public bool Ok { get; }
        public Dictionary<string, object?> Data { get; }
        public ToolError? Error { get; }
        public IList<ArtifactInfo> Artifacts { get; }

        public ToolResult(bool ok, Dictionary<string, object?>? data = null,
            ToolError? error = null, IList<ArtifactInfo>? artifacts = null)
        {
            Ok = ok;
            Data = data ?? new Dictionary<string, object?>();
            Error = error;
            Artifacts = artifacts ?? new List<ArtifactInfo>();
        }

        public static ToolResult Success(Dictionary<string, object?> data,
            IList<ArtifactInfo>? artifacts = null) => new(true, data, null, artifacts);

        public static ToolResult Failure(string code, string message) =>
            new(false, null, new ToolError(code, message));
    }

    public class ToolError
    {
        public string Code { get; }
        public string Message { get; }

        public ToolError(string code, string message)
        {
            Code = code;
            Message = message;
        }
    }

    /// <summary>登记到任务成果清单的单个文件。</summary>
    public class ArtifactInfo
    {
        /// <summary>相对工作区的路径，目录分隔符统一为 '/'。</summary>
        public string Path { get; }
        /// <summary>面向用户的平铺交付副本；内部 Path 继续用于隔离、回读和成果交接。</summary>
        public string PublishedPath { get; set; } = "";
        /// <summary>如 summary_csv / report_md。</summary>
        public string Type { get; }
        public long Bytes { get; }
        public string Sha256 { get; }
        public string SourceSnapshotSha256 { get; }
        public string ToolName { get; }
        public string ToolVersion { get; }

        public ArtifactInfo(string path, string type, long bytes, string sha256, string sourceSnapshotSha256 = "", string toolName = "", string toolVersion = "1")
        {
            Path = path;
            Type = type;
            Bytes = bytes;
            Sha256 = sha256;
            SourceSnapshotSha256 = sourceSnapshotSha256; ToolName = toolName; ToolVersion = toolVersion;
        }
    }

    /// <summary>
    /// 宿主提供的工具上下文：任务 ID、工作区策略、聚合结果暂存、审批提供者与取消信号。
    /// 模型只能通过工具参数间接影响其中内容，不能覆盖宿主注入的值。
    /// </summary>
    public class ToolContext
    {
        [Newtonsoft.Json.JsonIgnore] public System.Func<TaskWorkState>? WorkState { get; set; }
        public string TaskId { get; }
        public IWorkspacePolicy Workspace { get; }
        public System.Threading.CancellationToken CancellationToken { get; }
        /// <summary>本任务内 aggregate_csv/aggregate_table 产生的内存结果，key 为 aggregation_id。</summary>
        public IAggregationStore Aggregations { get; }
        /// <summary>本任务内 plan_file_organization 生成的整理计划，key 为 plan_id。</summary>
        public IFilePlanStore FilePlans { get; }
        /// <summary>敏感操作审批提供者；null 时按拒绝处理（fail-closed）。</summary>
        public IApprovalProvider? Approvals { get; }
        [Newtonsoft.Json.JsonIgnore] public ApprovedToolOperation? ApprovedOperation { get; set; }

        public ToolContext(string taskId, IWorkspacePolicy workspace,
            IAggregationStore aggregations, System.Threading.CancellationToken cancellationToken,
            IFilePlanStore? filePlans = null, IApprovalProvider? approvals = null, IUserInputProvider? userInput = null,
            ITaskPlanStore? taskPlans = null, ITaskHistoryReader? taskHistory = null)
        {
            TaskId = taskId;
            Workspace = workspace;
            Aggregations = aggregations;
            CancellationToken = cancellationToken;
            FilePlans = filePlans ?? new EmptyFilePlanStore();
            Approvals = approvals;
            UserInput = userInput;
            TaskPlans = taskPlans; TaskHistory = taskHistory;
        }
        public IUserInputProvider? UserInput { get; }
        public ITaskPlanStore? TaskPlans { get; }
        public ITaskHistoryReader? TaskHistory { get; }
    }

    public sealed class TaskWorkState
    {
        public string Summary { get; set; } = "";
        public bool HasPendingWork { get; set; }
    }

    /// <summary>空计划存储：未注入时的默认值（plan_file_organization 会明确报不可用）。</summary>
    public sealed class EmptyFilePlanStore : IFilePlanStore
    {
        public string Add(FilePlanRecord plan)
        {
            throw new ToolException(ErrorCodes.InvalidArguments,
                "当前宿主未启用文件整理能力。");
        }

        public FilePlanRecord? Find(string planId) => null;
    }

    /// <summary>聚合结果的内存暂存；aggregation_id 仅在单任务生命周期内有效。</summary>
    public interface IAggregationStore
    {
        string Add(AggregationResult result);
        AggregationResult? Find(string aggregationId);
    }

    /// <summary>aggregate_csv/aggregate_table 的确定性计算结果（金额由代码计算，非模型心算）。</summary>
    public class AggregationResult
    {
        public string AggregationId { get; }
        public string GroupByColumn { get; }
        public string SumColumn { get; }
        public IList<string> SourcePaths { get; }
        public int RecordCount { get; }
        /// <summary>分组名 -> 合计，按分组名序数规则排序。</summary>
        public IList<KeyValuePair<string, decimal>> Groups { get; }
        public decimal Total { get; }
        /// <summary>本次应用的筛选口径描述（无筛选为空）；写进报告保证口径可追溯。</summary>
        public string FilterDescription { get; }
        /// <summary>本次应用的分组映射描述（无映射为空）。</summary>
        public string MappingDescription { get; }
        /// <summary>被筛除的记录数。</summary>
        public int FilteredOutCount { get; }

        public AggregationResult(string aggregationId, string groupByColumn, string sumColumn,
            IList<string> sourcePaths, int recordCount,
            IList<KeyValuePair<string, decimal>> groups, decimal total,
            string filterDescription = "", string mappingDescription = "", int filteredOutCount = 0)
        {
            AggregationId = aggregationId;
            GroupByColumn = groupByColumn;
            SumColumn = sumColumn;
            SourcePaths = sourcePaths;
            RecordCount = recordCount;
            Groups = groups;
            Total = total;
            FilterDescription = filterDescription;
            MappingDescription = mappingDescription;
            FilteredOutCount = filteredOutCount;
        }
    }
}
