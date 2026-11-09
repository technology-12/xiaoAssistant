using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;
using WorkAssistant.Tools;

namespace WorkAssistant.Host
{
    // OpenCode-inspired allow/ask/deny. This can narrow, never widen, existing skill and runtime permissions.
    internal sealed class WorkspaceToolPermissions
    {
        private readonly Dictionary<string, string> _rules = new(StringComparer.Ordinal);
        public WorkspaceToolPermissions(string workspace)
        {
            var directory = Path.Combine(workspace, ".claude");
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new ToolException(ErrorCodes.PathDenied, "配置目录不能是目录联接。");
            var path = Path.Combine(directory, "xiao.permissions.json");
            if (!File.Exists(path)) return;
            using var input = VerifiedFiles.OpenRead(path, directory);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || input.Length > 65536)
                throw new ToolException(ErrorCodes.InvalidArguments, "工作区权限配置路径或大小无效。");
            try
            {
                using var reader = new StreamReader(input);
                var obj = JObject.Parse(reader.ReadToEnd(), new Newtonsoft.Json.Linq.JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                ToolArgs.RejectUnknown(obj, "version", "tools");
                if (obj["version"]?.Type != JTokenType.Integer || obj["version"]!.ToString() != "1" || obj["tools"] is not JObject rules)
                    throw new ToolException(ErrorCodes.InvalidArguments, "权限配置需要 version=1 和 tools 对象。");
                foreach (var property in rules.Properties())
                {
                    if (property.Name != "*" && !SkillRules.ToolNames.Contains(property.Name) || property.Value.Type != JTokenType.String ||
                        !new[] { "allow", "ask", "deny" }.Contains((string?)property.Value))
                        throw new ToolException(ErrorCodes.InvalidArguments, "权限规则必须使用已知工具名及 allow/ask/deny。");
                    _rules.Add(property.Name, (string)property.Value!);
                }
            }
            catch (Newtonsoft.Json.JsonException) { throw new ToolException(ErrorCodes.InvalidArguments, "工作区权限配置不是合法 JSON，未启动任务。"); }
        }
        public IAgentTool[] Apply(IEnumerable<IAgentTool> tools) => tools.Select(tool =>
        {
            var action = _rules.TryGetValue(tool.Definition.Name, out var specific) ? specific : _rules.TryGetValue("*", out var fallback) ? fallback : "allow";
            return action == "deny" ? null : action == "ask" ? new ApprovalTool(tool) : tool;
        }).Where(t => t != null).Cast<IAgentTool>().ToArray();

        internal static ToolDefinition Inherit(ToolDefinition child, ToolDefinition parent) =>
            new ToolDefinition(child.Name, child.Description, (JObject)child.Parameters.DeepClone(), child.ProducesArtifacts,
                child.RequiresApproval || parent.RequiresApproval) { PolicyRequiresApproval = child.PolicyRequiresApproval || parent.PolicyRequiresApproval };
        private sealed class ApprovalTool : IAgentTool
        {
            private readonly IAgentTool _inner;
            public ToolDefinition Definition { get; }
            public ApprovalTool(IAgentTool inner)
            {
                _inner = inner; var d = inner.Definition;
                Definition = new ToolDefinition(d.Name, d.Description, (JObject)d.Parameters.DeepClone(), d.ProducesArtifacts, true) { PolicyRequiresApproval = true };
            }
            public Task<ToolResult> ExecuteAsync(ToolContext context, string json, CancellationToken token) => _inner.ExecuteAsync(context, json, token);
        }
    }
}
