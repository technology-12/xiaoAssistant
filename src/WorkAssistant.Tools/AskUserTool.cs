using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;
namespace WorkAssistant.Tools
{
    public sealed class AskUserTool : AgentToolBase
    {
        protected override string FallbackErrorCode => ErrorCodes.InvalidArguments;
        public override ToolDefinition Definition { get; } = new("ask_user", "当金额、币种、日期、映射或业务口径不明确时询问用户；不得擅自假设。",
            Schema(("question", "string", "一个明确的问题", true), ("options", "array", "可选的建议答案，最多 6 项", false)));
        protected override async Task<ToolResult> ExecuteValidatedAsync(ToolContext context, JObject args, CancellationToken token)
        {
            ToolArgs.RejectUnknown(args, "question", "options");
            string text = ToolArgs.RequireString(args, "question");
            if (text.Length > 2000 || context.UserInput == null) return ToolResult.Failure(ErrorCodes.InvalidArguments, "当前无法获取用户补充信息。");
            var question = new UserQuestion { Question = text, Options = ToolArgs.OptionalStringArray(args, "options", 6)?.ToList() ?? new List<string>() };
            var answer = await context.UserInput.RequestAsync(question, token);
            return answer == null ? ToolResult.Failure(ErrorCodes.UserApprovalDenied, "用户取消了补充信息；不能假设答案。") :
                ToolResult.Success(new Dictionary<string, object?> { ["answer"] = answer });
        }
    }
    public static class BuiltInTools
    {
        public static IAgentTool[] Create(WorkspacePolicy policy, bool readOnly = false) => new IAgentTool[] {
            new ListFilesTool(policy), new PreviewCsvTool(policy), new PreviewTableTool(policy), new AggregateCsvTool(policy),
            new AggregateTableTool(policy), new ExportSummaryTool(policy), new ExportXlsxTool(policy), new GenerateDocxTool(policy),
            new FillDocxTemplateTool(policy), new PreviewDocxFieldsTool(policy), new ExtractPdfTextTool(policy, !readOnly),
            new PlanFileOrganizationTool(policy), new ApplyFilePlanTool(policy), new AskUserTool(), new DraftSkillTool(policy),
            new ReadDocumentTextTool(policy), new SearchInputTextTool(policy), new UpdateTaskPlanTool(), new ReadTaskHistoryTool(),
            new PlanTextEditTool(policy), new ValidateTextEditTool(policy), new CommitTextEditTool(policy), new CommitTextEditTool(policy, true),
            new OfficeAutomationTool(policy, "inspect_office"), new OfficeAutomationTool(policy, "write_word"), new OfficeAutomationTool(policy, "edit_word"),
            new OfficeAutomationTool(policy, "write_presentation"), new OfficeAutomationTool(policy, "edit_presentation"), new DocumentWorkspaceTool(policy) };
    }
}
