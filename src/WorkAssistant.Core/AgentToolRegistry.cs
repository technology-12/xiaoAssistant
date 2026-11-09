using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace WorkAssistant.Core
{
    /// <summary>Validated per-runtime catalog. Registration errors fail at startup, not during a task.</summary>
    public sealed class AgentToolRegistry
    {
        private readonly Dictionary<string, IAgentTool> _tools = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ToolDefinition> _definitions = new(StringComparer.Ordinal);
        public IList<ToolDefinition> Definitions { get; }

        public AgentToolRegistry(IEnumerable<IAgentTool> tools)
        {
            if (tools == null) throw new ArgumentNullException(nameof(tools));
            var definitions = new List<ToolDefinition>();
            foreach (var tool in tools)
            {
                var definition = tool?.Definition;
                if (definition == null || string.IsNullOrWhiteSpace(definition.Name) ||
                    !Regex.IsMatch(definition.Name, @"\A[a-zA-Z0-9_-]{1,64}\z") ||
                    string.IsNullOrWhiteSpace(definition.Description) || definition.Parameters == null ||
                    (string?)definition.Parameters["type"] != "object")
                    throw new ArgumentException("工具定义无效：必须提供唯一名称、说明和 object 参数结构。", nameof(tools));
                if (_tools.ContainsKey(definition.Name))
                    throw new ArgumentException("工具名称重复：" + definition.Name, nameof(tools));
                var snapshot = new ToolDefinition(definition.Name, definition.Description,
                    (Newtonsoft.Json.Linq.JObject)definition.Parameters.DeepClone(), definition.ProducesArtifacts, definition.RequiresApproval) { PolicyRequiresApproval = definition.PolicyRequiresApproval };
                _tools.Add(snapshot.Name, tool!);
                _definitions.Add(snapshot.Name, snapshot);
                definitions.Add(snapshot);
            }
            Definitions = definitions.AsReadOnly();
        }

        public ToolDefinition? FindDefinition(string name) => _definitions.TryGetValue(name, out var definition) ? definition : null;
        public IAgentTool? FindTool(string name) => _tools.TryGetValue(name, out var tool) ? tool : null;
        public IList<ToolDefinition> AllowedDefinitions(AgentExecutionPolicy policy) =>
            Definitions.Where(policy.Allows).ToList().AsReadOnly();
    }
}
