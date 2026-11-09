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
    public sealed class CommitTextEditTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;
        private readonly bool _rollback;
        public CommitTextEditTool(WorkspacePolicy policy, bool rollback = false)
        {
            _policy = policy; _rollback = rollback;
            Definition = new ToolDefinition(rollback ? "rollback_text_edit" : "apply_text_edit", rollback
                ? "经本次用户批准，将本任务已提交的文本提案恢复到备份版本；仅当前原文仍等于该提案提交版本时允许，后续更新会拒绝回滚。"
                : AtomicTextTransaction.CommitDescription,
                Schema(("plan_id", "string", "本任务的文本提案 ID", true), ("proposal_fingerprint", "string", "提案返回的完整指纹", true),
                    ("expected_sha256", "string", rollback ? "提案的 candidate_sha256" : "提案的 base_sha256", true)), producesArtifacts: true, requiresApproval: true);
        }
        public override ToolDefinition Definition { get; }
        protected override string FallbackErrorCode => ErrorCodes.PlanInvalid;
        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context, JObject args, CancellationToken token)
        {
            ToolArgs.RejectUnknown(args, "plan_id", "proposal_fingerprint", "expected_sha256");
            var call = new ToolCall(context.ApprovedOperation?.CallId ?? "unapproved", Definition.Name, args.ToString(Newtonsoft.Json.Formatting.None));
            var binding = ApprovedToolOperation.Capture(context, call);
            var plan = context.FilePlans.Find(binding.PlanId)!;
            TextEditEngine.Validate(plan.TextEdit!);
            (context.ApprovedOperation ?? throw new ToolException(ErrorCodes.UserApprovalDenied, "缺少本次用户批准，未提交文本修改。"))
                .Demand(Definition.Name, call.ArgumentsJson, plan, context);
            var store = context.FilePlans as IPersistentPlanStore ?? throw new ToolException(ErrorCodes.PlanInvalid, "文本提交需要持久化计划存储。");
            var proposal = plan.TextEdit!;
            string finalState = _rollback ? "edit-rolled-back" : "edit-applied";
            string receiptName = plan.PlanId + (_rollback ? ".rollback-receipt.json" : ".apply-receipt.json");
            string backupName = plan.PlanId + (_rollback ? ".rollback-before.txt" : ".before.txt");
            string journal = _policy.ResolveTaskOutputPath(context.TaskId, plan.PlanId + (_rollback ? ".rollback-journal.json" : ".apply-journal.json"));
            string source = _policy.ResolveOriginalReadablePath(proposal.SourcePath);
            byte[] original = Convert.FromBase64String(proposal.BeforeBase64), candidate = Convert.FromBase64String(proposal.AfterBase64);
            ArtifactInfo Existing(string name, string type, string expected)
            {
                try
                {
                string path = _policy.ResolveTaskOutputPath(context.TaskId, name);
                using var stream = VerifiedFiles.OpenRead(path, Path.GetDirectoryName(path)!);
                if (stream.Length > 2 * 1024 * 1024 || string.IsNullOrEmpty(expected) || TextEditEngine.Hash(DocumentLimits.ReadBounded(stream, 2 * 1024 * 1024)) != expected)
                    throw new ToolException(ErrorCodes.NeedsReview, "提交记录或备份缺失/变化，禁止自动重放或回滚。");
                return new ArtifactInfo(_policy.ToWorkspaceRelative(path), type, stream.Length, expected);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { throw new ToolException(ErrorCodes.NeedsReview, "提交记录或备份无法读取，禁止自动重放或回滚。"); }
            }
            if (plan.Status == finalState)
            {
                var expected = _rollback ? proposal.BeforeSha256 : proposal.AfterSha256;
                if (TextEditEngine.Hash(PlanTextEditTool.Read(source, _policy.RootPath)) != expected)
                    throw new ToolException(ErrorCodes.EditConflict, "已完成操作后的原文件已有新版本，禁止重复覆盖。");
                var receipt = Existing(receiptName, "text_edit_receipt", _rollback ? plan.TextEditRollbackReceiptSha256 : plan.TextEditApplyReceiptSha256);
                var backup = Existing(backupName, "text_edit_backup", _rollback ? proposal.AfterSha256 : proposal.BeforeSha256);
                return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["plan_id"] = plan.PlanId, ["already_applied"] = true,
                    ["applied"] = !_rollback, ["rolled_back"] = _rollback, ["source_sha256"] = expected }, new[] { backup, receipt }));
            }
            if (plan.Status.Contains("prepared") || plan.Status == "edit-needs-review")
                throw new ToolException(ErrorCodes.NeedsReview, "先前文本提交状态未确认，需复核意图和原文件，不自动重放。");
            if (plan.Status != (_rollback ? "edit-applied" : "planned"))
                throw new ToolException(ErrorCodes.PlanInvalid, "当前计划状态不允许此操作；回滚后不能重用旧提案再次提交。");
            if (_rollback)
            {
                Existing(plan.PlanId + ".apply-receipt.json", "text_edit_receipt", plan.TextEditApplyReceiptSha256);
                Existing(plan.PlanId + ".before.txt", "text_edit_backup", proposal.BeforeSha256);
            }
            token.ThrowIfCancellationRequested();
            // Backup is a passive task artifact. It is durable before the source transaction starts.
            var backups = TaskOutputs.CommitFiles(_policy, context.TaskId, new[] {
                (backupName, _rollback ? candidate : original, "text_edit_backup") });
            bool prepared = false, committed = false;
            var record = new JObject { ["version"] = 1, ["task_id"] = context.TaskId, ["plan_id"] = plan.PlanId,
                ["tool"] = Definition.Name, ["source"] = proposal.SourcePath, ["proposal_fingerprint"] = proposal.Fingerprint,
                ["request_id"] = context.ApprovedOperation.RequestId, ["call_id"] = context.ApprovedOperation.CallId,
                ["expected_sha256"] = binding.ExpectedSha256, ["result_sha256"] = _rollback ? proposal.BeforeSha256 : proposal.AfterSha256,
                ["backup"] = backups[0].Path };
            void Journal(string state) { record["state"] = state; DurableFiles.WriteText(journal, record.ToString()); }
            try
            {
                AtomicTextTransaction.Commit(source, _policy.InputsRoot, binding.ExpectedSha256, _rollback ? original : candidate,
                    () => {
                        prepared = true; plan.Status = _rollback ? "rollback-prepared" : "edit-prepared"; store.Save(plan);
                        Journal("Prepared"); DocumentLimits.Report("Prepared", journal);
                    }, () => { Journal("Staged"); DocumentLimits.Report("Prepared", journal); },
                    () => { committed = true; Journal("Committed"); DocumentLimits.Report("Committed", journal); }, token);
                record["state"] = "Verified";
                var receipts = TaskOutputs.CommitFiles(_policy, context.TaskId, new[] {
                    (receiptName, TaskOutputs.Utf8WithBom(record.ToString()), "text_edit_receipt") });
                if (_rollback) plan.TextEditRollbackReceiptSha256 = receipts[0].Sha256; else plan.TextEditApplyReceiptSha256 = receipts[0].Sha256;
                plan.Status = finalState; store.Save(plan); Journal("Verified"); DocumentLimits.Report("Verified", journal);
                return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["plan_id"] = plan.PlanId,
                    ["applied"] = !_rollback, ["rolled_back"] = _rollback, ["source_sha256"] = record["result_sha256"],
                    ["proposal_fingerprint"] = proposal.Fingerprint, ["backup"] = backups[0].Path,
                    ["notice"] = _rollback ? "已按当前批准恢复备份；不会覆盖后续版本。" : AtomicTextTransaction.CommitNotice }, backups.Concat(receipts).ToArray()));
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                if (!prepared)
                {
                    DocumentLimits.Report("Rejected", "");
                    throw;
                }
                try { plan.Status = "edit-needs-review"; store.Save(plan); Journal(committed ? "CommittedNeedsReview" : "NeedsReview"); } catch { }
                throw new ToolException(ErrorCodes.NeedsReview, "文本事务状态需复核，已保留备份和意图，不自动重放：" + _policy.ToWorkspaceRelative(journal) + "；" + ex.Message);
            }
        }
    }
}
