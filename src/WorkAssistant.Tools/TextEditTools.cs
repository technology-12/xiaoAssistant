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
    public sealed class PlanTextEditTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;
        public PlanTextEditTool(WorkspacePolicy policy) { _policy = policy; }
        protected override string FallbackErrorCode => ErrorCodes.PlanInvalid;
        public override ToolDefinition Definition { get; } = new("plan_text_edit",
            "为一个输入文本文件生成精确替换提案、差异和候选文件，不修改原文件。先阅读原文，path 传 file_id。默认必须唯一匹配 old_text；replace_all=true 明确替换所有非重叠匹配。仅支持 UTF-8/带 BOM UTF-16、最大 256 KiB；提交必须另用 apply_text_edit 并获得本次批准，不能创建或删除原文件。",
            Schema(("path", "string", "输入文本文件的 file_id", true), ("old_text", "string", "精确匹配原文（包括换行），最多 16000 字符", true),
                ("new_text", "string", "替换后的纯文本，可为空，最多 16000 字符", true), ("replace_all", "boolean", "明确替换所有匹配，默认 false", false),
                ("reason", "string", "变更目的，最多 500 字符", true)), producesArtifacts: true);
        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context, JObject args, CancellationToken token)
        {
            ToolArgs.RejectUnknown(args, "path", "old_text", "new_text", "replace_all", "reason");
            string path = ToolArgs.RequireString(args, "path");
            if (args["new_text"]?.Type != JTokenType.String || args["replace_all"] != null && args["replace_all"]!.Type != JTokenType.Boolean)
                throw new ToolException(ErrorCodes.InvalidArguments, "new_text 必须为字符串，replace_all 必须为布尔值。");
            var snapshot = Read(_policy.ResolveReadablePath(path), _policy.SnapshotRoot ?? _policy.RootPath);
            var current = Read(_policy.ResolveOriginalReadablePath(path), _policy.RootPath);
            if (TextEditEngine.Hash(snapshot) != TextEditEngine.Hash(current)) throw new ToolException(ErrorCodes.EditConflict, "原文件已不同于任务快照；请新建任务重新阅读，不生成过期提案。");
            var proposal = TextEditEngine.Propose(path, snapshot, ToolArgs.RequireString(args, "old_text"), (string)args["new_text"]!,
                (bool?)args["replace_all"] ?? false, ToolArgs.RequireString(args, "reason"));
            var record = new FilePlanRecord("", XpArray.Empty<FilePlanOperation>()) { TextEdit = proposal };
            string id = context.FilePlans.Add(record);
            token.ThrowIfCancellationRequested();
            var artifacts = TaskOutputs.CommitFiles(_policy, context.TaskId, new[] {
                (id + ".diff", TaskOutputs.Utf8WithBom(proposal.Diff), "text_edit_diff"),
                (id + ".candidate.txt", Convert.FromBase64String(proposal.AfterBase64), "text_edit_candidate") });
            return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> {
                ["plan_id"] = id, ["source"] = path, ["base_sha256"] = proposal.BeforeSha256, ["candidate_sha256"] = proposal.AfterSha256,
                ["proposal_fingerprint"] = proposal.Fingerprint, ["match_count"] = proposal.MatchCount,
                ["diff_preview"] = proposal.Diff.Length > 6000 ? proposal.Diff.Substring(0, 6000) : proposal.Diff, ["diff_truncated"] = proposal.Diff.Length > 6000,
                ["applied"] = false, ["notice"] = "仅生成提案，未修改原文件。validate_text_edit 只检查当前冲突，检查成功不等于审批或提交。" }, artifacts));
        }
        internal static byte[] Read(string path, string root)
        {
            var extensions = new[] { ".txt", ".md", ".csv", ".json", ".xml", ".yaml", ".yml", ".ini", ".config", ".cs", ".xaml", ".py", ".js", ".ts", ".tsx", ".jsx", ".html", ".css", ".go", ".rs", ".java", ".cpp", ".c", ".h", ".sql", ".ps1", ".bat", ".cmd" };
            if (!extensions.Contains(Path.GetExtension(path).ToLowerInvariant())) throw new ToolException(ErrorCodes.UnsupportedFormat, "本轮只支持受控的文本和源码扩展名，不能编辑 Office、PDF 或二进制文件。");
            using var stream = VerifiedFiles.OpenRead(path, root);
            return DocumentLimits.ReadBounded(stream, TextEditEngine.MaxBytes);
        }
    }
    public sealed class ValidateTextEditTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;
        public ValidateTextEditTool(WorkspacePolicy policy) { _policy = policy; }
        protected override string FallbackErrorCode => ErrorCodes.PlanInvalid;
        public override ToolDefinition Definition { get; } = new("validate_text_edit",
            "校验本任务文本提案摘要和原文件当前状态：未提交时对照基线，已提交时对照候选，已回滚时对照基线。只读检查，不审批、不提交；成功不授予后续写入资格。",
            Schema(("plan_id", "string", "本任务 plan_text_edit 返回的提案 ID", true)));
        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context, JObject args, CancellationToken token)
        {
            ToolArgs.RejectUnknown(args, "plan_id"); var id = ToolArgs.RequireString(args, "plan_id");
            var plan = context.FilePlans.Find(id);
            var proposal = plan?.TextEdit ?? throw new ToolException(ErrorCodes.PlanInvalid, "文本提案不存在；不能跨任务引用或将整理计划当成文本提案。");
            TextEditEngine.Validate(proposal);
            if (plan!.Status != "planned" && plan.Status != "edit-applied" && plan.Status != "edit-rolled-back")
                throw new ToolException(ErrorCodes.NeedsReview, "文本提交状态未确认，不能仅根据文件内容声明已完成。");
            var source = _policy.ResolveOriginalReadablePath(proposal.SourcePath);
            if (!File.Exists(source)) throw new ToolException(ErrorCodes.EditConflict, "原文件不存在或已移动；提案已过期。");
            var current = PlanTextEditTool.Read(source, _policy.RootPath);
            string expected = plan.Status == "edit-applied" ? proposal.AfterSha256 : proposal.BeforeSha256;
            if (TextEditEngine.Hash(current) != expected) throw new ToolException(ErrorCodes.EditConflict, "原文件已变化，提案存在版本冲突；不能覆盖其他 Agent 或用户的更新。");
            token.ThrowIfCancellationRequested();
            return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> {
                ["plan_id"] = id, ["source"] = proposal.SourcePath, ["base_sha256"] = proposal.BeforeSha256,
                ["candidate_sha256"] = proposal.AfterSha256, ["proposal_fingerprint"] = proposal.Fingerprint,
                ["conflict"] = false, ["applied"] = plan.Status == "edit-applied", ["rolled_back"] = plan.Status == "edit-rolled-back",
                ["expected_current_sha256"] = expected, ["notice"] = "仅核验当前事实，未执行新的写入；后续提交必须重新检查版本并获得审批。" }));
        }
    }
}
