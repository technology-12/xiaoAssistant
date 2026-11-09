using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;
using WorkAssistant.Tools;

namespace WorkAssistant.Tests.Console
{
    public static partial class XpCompatTests
    {
        private static void RegisterEditRecoveryCases()
        {
            foreach (var stage in new[] { "Prepared", "Committed" })
            {
                var phase = stage;
                Test.Add("XP-R05：提交检查点 IO 异常保留备份且禁止重放（" + phase + "）", () => EditIoFailure(phase));
            }
            foreach (var stage in new[] { "Staged", "Committed" })
            {
                var phase = stage;
                Test.Add("XP-R05：提交进程被终止后可复核且不重放（" + phase + "）", () => EditProcessLoss(phase));
            }
        }

        private static (ToolContext Context, string Arguments, string PlanId) EditCase(string root, bool create)
        {
            WorkspacePolicy.EnsureLayout(root); var policy = new WorkspacePolicy(root);
            if (create) File.WriteAllText(Path.Combine(root, "inputs", "edit.txt"), "original", new System.Text.UTF8Encoding(false));
            var plans = new PersistentFilePlanStore(Path.Combine(root, "plans"));
            var context = new ToolContext("edit-recovery", policy, new InMemoryAggregationStore(), CancellationToken.None, plans, new AutoApproveProvider());
            string id;
            if (create)
            {
                var planned = new PlanTextEditTool(policy).ExecuteAsync(context,
                    "{\"path\":\"inputs/edit.txt\",\"old_text\":\"original\",\"new_text\":\"replacement\",\"reason\":\"isolated regression\"}", CancellationToken.None).GetAwaiter().GetResult();
                Test.Check(planned.Ok, "提案生成成功：" + planned.Error?.Message); id = (string)planned.Data["plan_id"]!;
                File.WriteAllText(Path.Combine(root, "plan-id.txt"), id);
            }
            else id = File.ReadAllText(Path.Combine(root, "plan-id.txt"));
            var proposal = plans.Find(id)!.TextEdit!;
            var arguments = new JObject { ["plan_id"] = id, ["proposal_fingerprint"] = proposal.Fingerprint, ["expected_sha256"] = proposal.BeforeSha256 }.ToString();
            var call = new ToolCall("edit-call", "apply_text_edit", arguments);
            var approval = AgentController.BuildApprovalRequest(context, call, new CommitTextEditTool(policy).Definition);
            context.ApprovedOperation = ApprovedToolOperation.From(approval, call);
            return (context, arguments, id);
        }
        private static void CheckEditRecovery(string root, string phase, string planId)
        {
            var path = Path.Combine(root, "inputs", "edit.txt");
            Test.Equal(phase == "Committed" ? "replacement" : "original", File.ReadAllText(path), "中断阶段对应的文件版本");
            var policy = new WorkspacePolicy(root);
            var backup = policy.ResolveTaskOutputPath("edit-recovery", planId + ".before.txt");
            Test.Equal("original", File.ReadAllText(backup), "原始版本在写入前已持久备份");
            var journal = JObject.Parse(File.ReadAllText(policy.ResolveTaskOutputPath("edit-recovery", planId + ".apply-journal.json")));
            Test.Check(new[] { "NeedsReview", "Staged", "Committed", "CommittedNeedsReview" }.Contains((string)journal["state"]!), "日志保留未确认的提交意图");
            var retry = EditCase(root, false);
            var result = new CommitTextEditTool(policy).ExecuteAsync(retry.Context, retry.Arguments, CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!result.Ok, "未确认的提交不能直接重放");
            Test.Equal(ErrorCodes.NeedsReview, result.Error!.Code, "重放请求提示复核");
            Test.Equal(phase == "Committed" ? "replacement" : "original", File.ReadAllText(path), "复核不重放原文件写入");
        }
        private static void EditIoFailure(string phase)
        {
            var root = Path.Combine(Path.GetTempPath(), "xp-edit-io-" + Guid.NewGuid().ToString("N"));
            var data = EditCase(root, true); DocumentLimits.Begin(CancellationToken.None, "apply_text_edit");
            DocumentLimits.ConfigureCheckpoint((state, manifest) =>
            {
                if (manifest.EndsWith(".apply-journal.json", StringComparison.Ordinal) && state == phase) throw new IOException("simulated checkpoint IO failure");
            });
            try
            {
                var result = new CommitTextEditTool(new WorkspacePolicy(root)).ExecuteAsync(data.Context, data.Arguments, CancellationToken.None).GetAwaiter().GetResult();
                Test.Check(!result.Ok, "IO 异常不能报告完成"); Test.Equal(ErrorCodes.NeedsReview, result.Error!.Code, "写入状态需复核");
            }
            finally { DocumentLimits.ConfigureCheckpoint((s, p) => { }); }
            CheckEditRecovery(root, phase, data.PlanId);
        }
        public static int RunEditCrashWorker(string root, string phase)
        {
            var data = EditCase(root, true); DocumentLimits.Begin(CancellationToken.None, "apply_text_edit");
            DocumentLimits.ConfigureCheckpoint((state, manifest) =>
            {
                if (!manifest.EndsWith(".apply-journal.json", StringComparison.Ordinal)) return;
                var journal = JObject.Parse(File.ReadAllText(manifest));
                if ((string)journal["state"]! != phase) return;
                File.WriteAllText(Path.Combine(root, "kill-ready.txt"), phase);
                Thread.Sleep(Timeout.Infinite);
            });
            new CommitTextEditTool(new WorkspacePolicy(root)).ExecuteAsync(data.Context, data.Arguments, CancellationToken.None).GetAwaiter().GetResult();
            return 2;
        }
        private static void EditProcessLoss(string phase)
        {
            var root = Path.Combine(Path.GetTempPath(), "xp-edit-kill-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            using var process = Process.Start(new ProcessStartInfo(typeof(XpCompatTests).Assembly.Location,
                "--xp-edit-crash-worker \"" + root + "\" " + phase) { UseShellExecute = false, CreateNoWindow = true });
            Test.Check(process != null, "隔离测试进程启动");
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(12);
                while (!File.Exists(Path.Combine(root, "kill-ready.txt")) && !process!.HasExited && DateTime.UtcNow < deadline) Thread.Sleep(20);
                Test.Check(File.Exists(Path.Combine(root, "kill-ready.txt")), "测试进程到达目标提交检查点");
                process!.Kill(); Test.Check(process.WaitForExit(5000), "测试进程已终止，文件句柄已释放");
                CheckEditRecovery(root, phase, File.ReadAllText(Path.Combine(root, "plan-id.txt")));
            }
            finally { if (!process!.HasExited) { process.Kill(); process.WaitForExit(5000); } }
        }
    }
}
