using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;

namespace WorkAssistant.Host
{
    internal sealed class ProjectCommandsTool : IAgentTool
    {
        private readonly WorkspaceAgentConfiguration _project;
        public ToolDefinition Definition { get; } = new("list_project_commands", "列出项目斜杠命令与主角色，供用户在输入框手动调用。此工具只返回目录，不执行命令。",
            JObject.Parse(@"{""type"":""object"",""properties"":{},""additionalProperties"":false}"));
        public ProjectCommandsTool(WorkspaceAgentConfiguration project) { _project = project; }
        public Task<ToolResult> ExecuteAsync(ToolContext context, string argumentsJson, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (JObject.Parse(argumentsJson).Properties().Any()) return XpTask.FromResult(ToolResult.Failure(ErrorCodes.InvalidArguments, "目录工具不接受参数。"));
                return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> {
                    ["commands"] = new JArray(_project.Commands.Select(c => new JObject { ["command"] = "/" + c.Name, ["description"] = c.Description, ["agent"] = c.Agent })),
                    ["roles"] = new JArray(_project.Roles.Where(r => !r.Disabled && !r.Hidden && r.Mode != "subagent").Select(r => new JObject { ["name"] = r.Name, ["description"] = r.Description, ["read_only"] = r.ReadOnly })),
                    ["usage"] = "/agent <role> <request> 或 /<command> <arguments>；只做文本展开，不执行 shell。" }));
            }
            catch (Newtonsoft.Json.JsonException) { return XpTask.FromResult(ToolResult.Failure(ErrorCodes.InvalidArguments, "参数必须为 JSON 对象。")); }
        }
    }
}
