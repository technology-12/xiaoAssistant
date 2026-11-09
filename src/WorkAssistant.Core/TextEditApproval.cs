using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Core
{
    public sealed class TextEditApprovalBinding
    {
        public string PlanId { get; set; } = "";
        public string Fingerprint { get; set; } = "";
        public string ExpectedSha256 { get; set; } = "";
        public string PlanStatus { get; set; } = "";
        public string TaskId { get; set; } = "";
        public string WorkspaceRoot { get; set; } = "";
        [JsonIgnore] public string SourcePath { get; set; } = "";
        [JsonIgnore] public string Diff { get; set; } = "";
    }
    // This is carried by authenticated Host IPC, never accepted as a model tool argument.
    public sealed class ApprovedToolOperation
    {
        public string RequestId { get; set; } = "";
        public string CallId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string ArgumentsSha256 { get; set; } = "";
        public TextEditApprovalBinding Binding { get; set; } = new();
        public static bool IsTextMutation(string name) => name == "apply_text_edit" || name == "rollback_text_edit";
        public static string ArgumentsHash(string json) => TextEditEngine.Hash(System.Text.Encoding.UTF8.GetBytes(JObject.Parse(json).ToString(Formatting.None)));
        public static ApprovedToolOperation? From(ApprovalRequest request, ToolCall call) => request.TextEditBinding == null ? null : new ApprovedToolOperation {
            RequestId = request.RequestId, CallId = call.Id, ToolName = call.Name, ArgumentsSha256 = ArgumentsHash(call.ArgumentsJson), Binding = request.TextEditBinding };
        public static TextEditApprovalBinding Capture(ToolContext context, ToolCall call)
        {
            var args = JObject.Parse(call.ArgumentsJson);
            foreach (var p in args.Properties()) if (p.Name != "plan_id" && p.Name != "proposal_fingerprint" && p.Name != "expected_sha256") throw new ToolException(ErrorCodes.InvalidArguments, "文本提交参数含未知字段。");
            foreach (var key in new[] { "plan_id", "proposal_fingerprint", "expected_sha256" })
                if (args[key]?.Type != JTokenType.String) throw new ToolException(ErrorCodes.InvalidArguments, "文本提交需 plan_id、proposal_fingerprint 和 expected_sha256。");
            var plan = context.FilePlans.Find((string)args["plan_id"]!) ?? throw new ToolException(ErrorCodes.PlanInvalid, "文本提案不存在。");
            var proposal = plan.TextEdit ?? throw new ToolException(ErrorCodes.PlanInvalid, "该计划不是文本提案。");
            TextEditEngine.Validate(proposal);
            string expected = call.Name == "rollback_text_edit" ? proposal.AfterSha256 : proposal.BeforeSha256;
            if ((string)args["proposal_fingerprint"]! != proposal.Fingerprint || (string)args["expected_sha256"]! != expected)
                throw new ToolException(ErrorCodes.EditConflict, "提交参数不属于当前提案和版本，需重新审阅。");
            return new TextEditApprovalBinding { PlanId = plan.PlanId, Fingerprint = proposal.Fingerprint, ExpectedSha256 = expected, PlanStatus = plan.Status,
                TaskId = context.TaskId, WorkspaceRoot = context.Workspace.RootPath, SourcePath = proposal.SourcePath, Diff = proposal.Diff };
        }
        public void Demand(string tool, string json, FilePlanRecord plan, ToolContext context)
        {
            if (string.IsNullOrEmpty(RequestId) || string.IsNullOrEmpty(CallId) || ToolName != tool || ArgumentsSha256 != ArgumentsHash(json) ||
                Binding.PlanId != plan.PlanId || Binding.Fingerprint != plan.TextEdit?.Fingerprint || Binding.PlanStatus != plan.Status ||
                Binding.TaskId != context.TaskId || !string.Equals(Binding.WorkspaceRoot, context.Workspace.RootPath, StringComparison.OrdinalIgnoreCase) ||
                Binding.ExpectedSha256 != (string?)JObject.Parse(json)["expected_sha256"])
                throw new ToolException(ErrorCodes.UserApprovalDenied, "缺少绑定本次调用、提案与状态的用户批准，未执行文本写入。");
        }
    }
}
