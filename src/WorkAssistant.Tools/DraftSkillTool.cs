using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public sealed class DraftSkillTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;
        public DraftSkillTool(WorkspacePolicy policy) { _policy = policy; }
        protected override string FallbackErrorCode => ErrorCodes.OutputWriteFailed;
        public override ToolDefinition Definition { get; } = new("draft_skill", "生成可审阅的技能草稿 SKILL.md 与配套元数据；不会自动安装。仅可组合内置工具，不能生成可执行脚本或扩展权限。",
            Schema(("id", "string", "小写字母、数字、连字符的技能 ID", true), ("name", "string", "显示名称", true),
                ("description", "string", "能力与适用场景", true), ("instructions", "string", "简洁的工作流说明", true),
                ("tools", "array", "所需内置工具名；可用工具见当前工具定义", true)), producesArtifacts: true);
        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context, JObject args, CancellationToken token)
        {
            ToolArgs.RejectUnknown(args, "id", "name", "description", "instructions", "tools");
            var skill = new SkillDefinition { Id = ToolArgs.RequireString(args, "id"), Name = ToolArgs.RequireString(args, "name"),
                Description = ToolArgs.RequireString(args, "description"), Instructions = ToolArgs.RequireString(args, "instructions"),
                AllowedTools = ToolArgs.RequireStringArray(args, "tools", SkillRules.ToolNames.Length).ToList() };
            SkillRules.Validate(skill); token.ThrowIfCancellationRequested();
            var artifacts = TaskOutputs.CommitFiles(_policy, context.TaskId, new[] {
                (skill.Id + ".SKILL.md", TaskOutputs.Utf8WithBom(SkillRules.Markdown(skill)), "skill_instructions"),
                (skill.Id + ".skill.json", TaskOutputs.Utf8WithBom(JsonConvert.SerializeObject(skill, Formatting.Indented)), "skill_metadata") });
            return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["skill_id"] = skill.Id, ["installed"] = false,
                ["next_action"] = "在技能库中导入 .skill.json，审阅说明与工具权限后保存。" }, artifacts));
        }
    }
}
