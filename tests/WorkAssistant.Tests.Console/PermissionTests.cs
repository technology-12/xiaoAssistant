using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;
using WorkAssistant.Tools;

namespace WorkAssistant.Tests.Console
{
    public static class PermissionTests
    {
        static string Root() { string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pm-" + Guid.NewGuid().ToString("N").Substring(0, 8)); WorkspacePolicy.EnsureLayout(p); return p; }
        public static void Register()
        {
            foreach (TaskPermissionMode mode in Enum.GetValues(typeof(TaskPermissionMode)))
            {
                var selected = mode;
                Test.Add("Permissions_" + mode + "_OutputGate", () => {
                    string root = Root(); var approvals = new Approval(true); var events = new Sink();
                    var call = new ToolCall("write", "write_word", new JObject { ["file_name"] = "稿件.docx", ["title"] = "标题", ["blocks"] = new JArray(new JObject { ["type"] = "paragraph", ["text"] = "合成正文" }) }.ToString());
                    var result = Run(root, selected, call, new OfficeAutomationTool(new WorkspacePolicy(root), "write_word"), approvals, events, TaskKind.FileGeneration);
                    Test.Equal(TaskState.Completed, result.State, result.ErrorMessage ?? "output success"); Test.Equal(selected == TaskPermissionMode.RequestApproval ? 1 : 0, approvals.Count, "output approval count");
                });
                Test.Add("Permissions_" + mode + "_OriginalMutationBoundGrant", () => {
                    string root = Root(); var policy = new WorkspacePolicy(root); File.WriteAllText(Path.Combine(root, "inputs", "a.txt"), "before", new UTF8Encoding(false));
                    var plans = new PersistentFilePlanStore(Path.Combine(root, "plans"));
                    var proposal = TextEditEngine.Propose("inputs/a.txt", Encoding.UTF8.GetBytes("before"), "before", "after", false, "test");
                    string id = plans.Add(new FilePlanRecord("", XpArray.Empty<FilePlanOperation>()) { TextEdit = proposal });
                    var call = new ToolCall("commit", "apply_text_edit", new JObject { ["plan_id"] = id, ["proposal_fingerprint"] = proposal.Fingerprint, ["expected_sha256"] = proposal.BeforeSha256 }.ToString());
                    var approvals = new Approval(true); var events = new Sink();
                    var result = Run(root, selected, call, new CommitTextEditTool(policy), approvals, events, TaskKind.FileModification, plans);
                    Test.Equal(TaskState.Completed, result.State, result.ErrorMessage ?? "commit success"); Test.Equal("after", File.ReadAllText(Path.Combine(root, "inputs", "a.txt")), "actual input mutation");
                    Test.Equal(selected == TaskPermissionMode.FullAccess ? 0 : 1, approvals.Count, "original approval count");
                    Test.Equal(selected == TaskPermissionMode.FullAccess, events.Events.Any(e => e.Kind == TaskEventKind.PermissionAutoApproved), "automatic approval audit");
                });
            }
            Test.Add("Permissions_RequestDeny_NoFileCreated", () => {
                string root = Root(); var approvals = new Approval(false); var call = new ToolCall("write", "write_word", "{\"file_name\":\"denied.docx\",\"title\":\"test\",\"blocks\":[{\"type\":\"paragraph\",\"text\":\"test\"}]}");
                var result = Run(root, TaskPermissionMode.RequestApproval, call, new OfficeAutomationTool(new WorkspacePolicy(root), "write_word"), approvals, new Sink(), TaskKind.FileGeneration);
                Test.Equal(ErrorCodes.UserApprovalDenied, result.ErrorCode, "denied"); Test.Check(!Directory.GetFiles(root, "denied.docx", SearchOption.AllDirectories).Any(), "no side effect before approval");
            });
            Test.Add("Permissions_RequestMalformed_NoApprovalOrFile", () => {
                string root = Root(); var approvals = new Approval(false);
                var result = Run(root, TaskPermissionMode.RequestApproval, new ToolCall("bad", "write_word", "{broken"), new OfficeAutomationTool(new WorkspacePolicy(root), "write_word"), approvals, new Sink(), TaskKind.FileGeneration);
                Test.Equal(0, approvals.Count, "invalid JSON is returned for model correction, not user approval");
                Test.Check(!Directory.GetFiles(root, "*.docx", SearchOption.AllDirectories).Any(), "malformed arguments create no file");
            });
            Test.Add("Permissions_FullAccess_ProjectAskStillPrompts", () => {
                string root = Root(); var original = new OfficeAutomationTool(new WorkspacePolicy(root), "write_word");
                var role = new AgentRole { Permissions = new Dictionary<string, string> { ["write_word"] = "ask" } }; var restricted = role.Apply(new[] { original }).Single();
                var approvals = new Approval(false); var result = Run(root, TaskPermissionMode.FullAccess, new ToolCall("write", "write_word", "{}"), restricted, approvals, new Sink(), TaskKind.FileGeneration);
                Test.Equal(1, approvals.Count, "explicit project ask preserved"); Test.Equal(ErrorCodes.UserApprovalDenied, result.ErrorCode, "denial preserved");
            });
            Test.Add("Permissions_FullAccess_ReadOnlyStillDeniesWrites", () => {
                string root = Root(); var policy = new AgentExecutionPolicy(AgentExecutionMode.ReadOnly);
                Test.Check(!policy.Allows(new OfficeAutomationTool(new WorkspacePolicy(root), "write_word").Definition), "read-only stronger");
                Test.Check(new AgentRole { Permissions = new Dictionary<string, string> { ["write_word"] = "deny" } }.Apply(new[] { new OfficeAutomationTool(new WorkspacePolicy(root), "write_word") }).Length == 0, "explicit deny preserved");
            });
            Test.Add("Permissions_Request_PrivateDraftReadDoesNotPrompt", () => {
                string root = Root(); var definition = new DocumentWorkspaceTool(new WorkspacePolicy(root)).Definition;
                foreach (var action in new[] { "begin", "append", "patch", "inspect", "status", "plan", "resume" }) Test.Check(!TaskPermissions.Effective(definition, new ToolCall("x", "document_workspace", "{\"action\":\"" + action + "\"}"), TaskPermissionMode.RequestApproval).RequiresApproval, "private workflow " + action);
                Test.Check(TaskPermissions.Effective(definition, new ToolCall("x", "document_workspace", "{\"action\":\"finalize\"}"), TaskPermissionMode.RequestApproval).RequiresApproval, "final publication asks");
            });
            Test.Add("Permissions_SettingsAndTaskRoundtrip_UnknownFailsClosed", () => {
                string root = Root(); var store = new SettingsStore(Path.Combine(root, "settings")); var settings = new AppSettings { PermissionMode = TaskPermissionMode.FullAccess }; store.Save(settings);
                Test.Equal(TaskPermissionMode.FullAccess, store.Load().PermissionMode, "saved preference");
                var obj = JObject.Parse(File.ReadAllText(Path.Combine(root, "settings", "settings.json"))); obj["permission_mode"] = "unknown"; File.WriteAllText(Path.Combine(root, "settings", "settings.json"), obj.ToString());
                Test.Equal(TaskPermissionMode.RequestApproval, store.Load().PermissionMode, "invalid preference cannot grant access");
                var task = new TaskRecord("task", "test", root) { PermissionMode = TaskPermissionMode.FullAccess }; var tasks = new LocalTaskStore(Path.Combine(root, "state")); tasks.SaveTask(task);
                Test.Equal(TaskPermissionMode.FullAccess, tasks.LoadTask("task")!.PermissionMode, "task snapshot persisted");
                Test.ExpectToolError(ErrorCodes.ToolPolicyDenied, () => TaskPermissions.Validate((TaskPermissionMode)99), "unknown task mode denied");
            });
        }
        static TaskRecord Run(string root, TaskPermissionMode mode, ToolCall call, IAgentTool tool, Approval approvals, Sink events, TaskKind kind, IFilePlanStore? plans = null)
        {
            var task = new TaskRecord("task", kind == TaskKind.FileModification ? "提交修改原文" : "生成文件", root) { PermissionMode = mode };
            var controller = new AgentController(new Sequence(call), new ToolDispatcher(new[] { tool }, new InMemoryReceiptStore()), events,
                (id, ct) => new ToolContext(id, new WorkspacePolicy(root), new InMemoryAggregationStore(), ct, plans, approvals), "mock");
            return controller.RunAsync(task, new AgentRunOptions { Kind = kind }, CancellationToken.None).GetAwaiter().GetResult();
        }
        sealed class Approval : IApprovalProvider { public int Count; readonly bool allow; public Approval(bool allow) { this.allow = allow; } public Task<bool> RequestAsync(ApprovalRequest request, CancellationToken t) { Count++; return XpTask.FromResult(allow); } }
        sealed class Sink : ITaskEventSink { public List<TaskEvent> Events = new(); public void Append(TaskEvent e) => Events.Add(e); }
        sealed class Sequence : IModelClient { readonly ToolCall call; int n; public Sequence(ToolCall call) { this.call = call; } public Task<ModelReply> CompleteAsync(ModelRequest r, CancellationToken t) => XpTask.FromResult(++n == 1 ? new ModelReply(null, new[] { call }, "tool_calls") : new ModelReply("完成", null, "stop")); }
    }
}
