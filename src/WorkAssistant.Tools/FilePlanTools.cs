using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>文件整理计划的任务内暂存（与聚合暂存同规则：plan_id 不能跨任务引用）。</summary>
    public sealed class InMemoryFilePlanStore : IFilePlanStore
    {
        private readonly Dictionary<string, FilePlanRecord> _plans = new(StringComparer.Ordinal);

        public string Add(FilePlanRecord plan)
        {
            var id = "plan_" + Guid.NewGuid().ToString("N");
            _plans[id] = new FilePlanRecord(id, plan.Operations) { TextEdit = plan.TextEdit };
            return id;
        }

        public FilePlanRecord? Find(string planId)
        {
            return _plans.TryGetValue(planId, out var plan) ? plan : null;
        }
    }

    /// <summary>
    /// plan_file_organization（正式版 §8.4 第一步）：
    /// 校验模型给出的整理方案（仅 move、inputs 内、无覆盖），生成人可读计划成果。
    /// 本工具只产出计划，不执行任何文件操作。
    /// </summary>
    public sealed class PlanFileOrganizationTool : AgentToolBase
    {
        public const int MaxOperations = 200;
        public const string PlanArtifactName = "organize_plan.md";

        private readonly WorkspacePolicy _policy;

        public PlanFileOrganizationTool(WorkspacePolicy policy) => _policy = policy;

        protected override string FallbackErrorCode => ErrorCodes.PlanInvalid;

        public override ToolDefinition Definition { get; } = new ToolDefinition(
            "plan_file_organization",
            "生成文件整理计划（只生成计划，不执行）：operations 中每项声明把哪个文件移动到哪个目录" +
            "（可改名）。计划保存后用 apply_file_plan 执行（执行前会请用户确认）。仅支持 move，不支持删除。",
            Schema(("operations", "array",
                "操作数组，每项 {source: 相对路径, action: \"move\", target_directory: \"inputs/子目录\", new_name: 可选新文件名}", true),
                   ("reason", "string", "整理目的的简短说明（写进计划文档）", true)),
            producesArtifacts: true);

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "operations", "reason");
            var reason = ToolArgs.RequireString(args, "reason");
            var operations = ParseOperations(args, _policy);

            var plan = new FilePlanRecord("", operations);
            var planId = context.FilePlans.Add(plan);
            plan = context.FilePlans.Find(planId)!;

            // 计划成果：人可读清单（用户在审批时看到的就是同一份计划）。
            var sb = new System.Text.StringBuilder();
            sb.Append("# 文件整理计划\n\n");
            sb.Append("- 目的：").Append(OutputSanitizer.MarkdownCell(reason)).Append('\n');
            sb.Append("- 操作数：").Append(operations.Count).Append("（全部为移动/改名，不删除文件）\n");
            sb.Append("- 生成时间（UTC）：").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"))
              .Append("\n\n");
            sb.Append("| # | 源文件 | 目标 |\n|---|---|---|\n");
            for (var i = 0; i < operations.Count; i++)
            {
                sb.Append("| ").Append(i + 1).Append(" | ")
                  .Append(OutputSanitizer.MarkdownCell(operations[i].Source)).Append(" | ")
                  .Append(OutputSanitizer.MarkdownCell(operations[i].Target)).Append(" |\n");
            }
            sb.Append("\n执行前需要用户确认；确认后目标文件若已存在将拒绝执行该项。\n");

            var artifacts = TaskOutputs.CommitFiles(_policy, context.TaskId,
                new[] { (PlanArtifactName, TaskOutputs.Utf8WithBom(sb.ToString()), "organize_plan_md") });

            return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?>
            {
                ["plan_id"] = planId,
                ["operation_count"] = operations.Count,
                ["artifacts"] = artifacts.Select(a => a.Path).ToList()
            }, artifacts));
        }

        /// <summary>解析并校验操作数组（计划阶段即拒绝非法项，避免执行期才失败）。</summary>
        internal static List<FilePlanOperation> ParseOperations(JObject args, WorkspacePolicy policy)
        {
            if (!args.TryGetValue("operations", out var token) || token.Type != JTokenType.Array)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, "参数 operations 必须是数组。");
            }
            var array = (JArray)token;
            if (array.Count == 0)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, "operations 不能为空。");
            }
            if (array.Count > MaxOperations)
            {
                throw new ToolException(ErrorCodes.LimitExceeded,
                    $"操作数超过 {MaxOperations} 项限制。");
            }

            var result = new List<FilePlanOperation>();
            var seenSources = new HashSet<string>(StringComparer.Ordinal);
            var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in array)
            {
                if (item.Type != JTokenType.Object)
                {
                    throw new ToolException(ErrorCodes.InvalidArguments, "operations 的每项必须是对象。");
                }
                var op = (JObject)item;
                foreach (var prop in op.Properties())
                {
                    if (prop.Name is not ("source" or "action" or "target_directory" or "new_name"))
                    {
                        throw new ToolException(ErrorCodes.InvalidArguments,
                            "操作包含未知字段：" + prop.Name + "（允许 source/action/target_directory/new_name）");
                    }
                }
                var source = RequireOpString(op, "source");
                var action = RequireOpString(op, "action");
                var targetDirectory = RequireOpString(op, "target_directory");
                var newName = op.TryGetValue("new_name", out var nameToken) &&
                              nameToken.Type == JTokenType.String
                    ? (string?)nameToken : null;

                if (action != "move")
                {
                    throw new ToolException(ErrorCodes.PlanInvalid,
                        $"不支持的 action：{action}（1.0 仅支持 move，不删除文件）");
                }

                // 源：必须在 inputs 内且当前存在。
                var sourceFull = policy.ResolveOriginalReadablePath(source);
                if (!File.Exists(sourceFull))
                {
                    throw new ToolException(ErrorCodes.PlanInvalid, "源文件不存在：" + source);
                }
                if (!seenSources.Add(source))
                {
                    throw new ToolException(ErrorCodes.PlanInvalid, "源文件重复出现：" + source);
                }

                // 目标：inputs 内、目录可不存在（执行时创建）、文件不可已存在。
                var fileName = newName ?? Path.GetFileName(sourceFull);
                var targetFull = policy.ResolveOrganizeTarget(targetDirectory, fileName);
                var targetRelative = policy.ToWorkspaceRelative(targetFull);
                if (string.Equals(targetRelative, policy.ToWorkspaceRelative(sourceFull),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new ToolException(ErrorCodes.PlanInvalid,
                        "目标与源相同：" + targetRelative);
                }
                if (File.Exists(targetFull))
                {
                    throw new ToolException(ErrorCodes.PlanInvalid,
                        "目标文件已存在，拒绝覆盖：" + targetRelative);
                }
                if (!seenTargets.Add(targetRelative))
                {
                    throw new ToolException(ErrorCodes.PlanInvalid,
                        "多个操作的目标相同：" + targetRelative);
                }
                string snapshotHash = TaskOutputs.Sha256File(policy.ResolveReadablePath(source));
                if (snapshotHash != TaskOutputs.Sha256File(sourceFull)) throw new ToolException(ErrorCodes.PlanInvalid, "源文件已不同于任务输入快照，请新建任务并重新确认。");
                result.Add(new FilePlanOperation(policy.ToWorkspaceRelative(sourceFull), targetRelative, snapshotHash));
            }
            return result;
        }

        private static string RequireOpString(JObject op, string name)
        {
            if (op.TryGetValue(name, out var token) && token.Type == JTokenType.String)
            {
                var value = (string)token!;
                if (value.Length > 0) return value;
            }
            throw new ToolException(ErrorCodes.InvalidArguments,
                $"操作缺少有效字段 {name}。");
        }
    }

    /// <summary>
    /// apply_file_plan（正式版 §8.4 第二~四步；执行前经审批门）：
    /// 预检 → 逐项移动 → 校验 → 变更清单。部分失败时返回 NEEDS_REVIEW（宿主进入 NeedsReview 状态，
    /// 不自动重试）；不删除任何文件。
    /// </summary>
    public sealed class ApplyFilePlanTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;

        public ApplyFilePlanTool(WorkspacePolicy policy) => _policy = policy;

        protected override string FallbackErrorCode => ErrorCodes.OutputWriteFailed;

        public override ToolDefinition Definition { get; } = new ToolDefinition(
            "apply_file_plan",
            "执行文件整理计划（plan_file_organization 返回的 plan_id）。执行前用户会看到完整计划并确认；" +
            "每项操作移动后立即校验，变更记录写入任务目录。",
            Schema(("plan_id", "string", "plan_file_organization 返回的计划 ID", true)),
            producesArtifacts: true,
            requiresApproval: true);

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "plan_id");
            var planId = ToolArgs.RequireString(args, "plan_id");
            var plan = context.FilePlans.Find(planId);
            if (plan == null || plan.TextEdit != null)
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "plan_id 无效或已过期（不能跨任务引用）。");
            }
            if (plan.Status == "verified")
            {
                foreach (var op in plan.Operations)
                {
                    string source = _policy.ResolveOriginalReadablePath(op.Source), target = _policy.ResolveOriginalReadablePath(op.Target);
                    if (File.Exists(source) || !File.Exists(target) || TaskOutputs.Sha256File(target) != op.SourceSha256)
                        throw new ToolException(ErrorCodes.NeedsReview, "已完成计划的文件状态已变化，拒绝再次执行。");
                }
                var logPath = _policy.ResolveTaskOutputPath(context.TaskId, "file_mutations.md");
                if (!File.Exists(logPath)) throw new ToolException(ErrorCodes.NeedsReview, "变更记录缺失，需人工复核。");
                var artifact = new ArtifactInfo(_policy.ToWorkspaceRelative(logPath), "mutations_log", new FileInfo(logPath).Length, TaskOutputs.Sha256File(logPath));
                return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?>
                { ["moved"] = plan.AppliedSources.Count, ["already_applied"] = true }, new[] { artifact }));
            }

            var journal = _policy.ResolveTaskOutputPath(context.TaskId, "mutation-" + planId + ".json");
            void Save(string state)
            {
                plan.Status = state;
                (context.FilePlans as IPersistentPlanStore)?.Save(plan);
                DurableFiles.WriteText(journal, Newtonsoft.Json.JsonConvert.SerializeObject(plan));
                DocumentLimits.Report(state == "verified" ? "Verified" : "Prepared", journal);
            }
            var planned = new List<(FilePlanOperation Op, string Source, string Target)>();
            foreach (var op in plan.Operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = _policy.ResolveOriginalReadablePath(op.Source);
                var target = _policy.ResolveOriginalReadablePath(op.Target);
                if (string.IsNullOrEmpty(op.SourceSha256) || !File.Exists(source) || File.Exists(target) ||
                    TaskOutputs.Sha256File(source) != op.SourceSha256)
                    throw new ToolException(ErrorCodes.PlanInvalid, "计划已过期或目标冲突，请重新生成并确认：" + op.Source);
                planned.Add((op, source, target));
            }
            Save("prepared");
            var attempted = false;
            try
            {
                foreach (var (op, source, target) in planned)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _policy.ResolveOriginalReadablePath(op.Target);
                    Save("moving");
                    attempted = true;
                    VerifiedFiles.MoveVerified(source, target, _policy.InputsRoot, op.SourceSha256);
                    if (File.Exists(source) || !File.Exists(target) || TaskOutputs.Sha256File(target) != op.SourceSha256)
                        throw new IOException("移动后摘要校验失败。 ");
                    plan.AppliedSources.Add(op.Source);
                    Save("prepared");
                }
                Save("applied");
                var log = "# 文件整理变更记录\n\n" + string.Join("\n", plan.Operations.Select(op =>
                    "- " + OutputSanitizer.MarkdownCell(op.Source) + " → " + OutputSanitizer.MarkdownCell(op.Target))) + "\n";
                var artifacts = TaskOutputs.CommitFiles(_policy, context.TaskId,
                    new[] { ("file_mutations.md", TaskOutputs.Utf8WithBom(log), "mutations_log") });
                Save("verified");
                return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?>
                { ["moved"] = plan.AppliedSources.Count, ["plan_id"] = planId }, artifacts));
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                if (!attempted && plan.AppliedSources.Count == 0 && ex is OperationCanceledException) throw;
                try { Save("needs-review"); } catch (Exception) { /* Earlier durable intent remains authoritative. */ }
                throw new ToolException(ErrorCodes.NeedsReview,
                    "文件操作需复核，禁止自动重试。已确认移动 " + plan.AppliedSources.Count + " 项。变更意图已保留：" +
                    _policy.ToWorkspaceRelative(journal) + "；" + ex.Message);
            }
        }

        private static string BuildPartialMessage(FilePlanRecord plan,
            List<string> doneMutations, string failedSource, string reason)
        {
            return $"部分完成后失败（{reason}）：已移动 {doneMutations.Count} 项，失败于 {failedSource}。" +
                   $"剩余 {plan.Operations.Count - doneMutations.Count} 项未执行。" +
                   "已执行项记录在计划中，请人工核对后重新生成计划处理剩余文件。";
        }
    }
}
