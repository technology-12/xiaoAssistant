using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Core
{
    // Host-owned restrictions: an allow entry can never restore an absent tool or remove approval.
    public sealed class AgentRole
    {
        public string Name { get; set; } = "general";
        public string Description { get; set; } = "General assistant";
        public string Instructions { get; set; } = "";
        public string Mode { get; set; } = "all";
        public string ModelAlias { get; set; } = "inherit";
        public bool ReadOnly { get; set; }
        public bool Hidden { get; set; }
        public bool Disabled { get; set; }
        public List<string>? Tools { get; set; }
        public Dictionary<string, string> Permissions { get; set; } = new(StringComparer.Ordinal);
        public int Steps { get; set; } = 128;
        public int ToolLimit { get; set; } = 256;
        public int TimeoutSeconds { get; set; } = 1800;
        public string Permission(string tool) => Permissions.TryGetValue(tool, out var value) ? value : Permissions.TryGetValue("*", out value) ? value : "allow";
        public ToolDefinition? Restrict(ToolDefinition definition)
        {
            if (Disabled || Tools != null && !Tools.Contains(definition.Name) || Permission(definition.Name) == "deny" ||
                ReadOnly && (definition.ProducesArtifacts || definition.IsMutation || definition.IsPlan)) return null;
            return new ToolDefinition(definition.Name, definition.Description, (JObject)definition.Parameters.DeepClone(),
                definition.ProducesArtifacts, definition.RequiresApproval || Permission(definition.Name) == "ask") { PolicyRequiresApproval = definition.PolicyRequiresApproval || Permission(definition.Name) == "ask" };
        }
        public IAgentTool[] Apply(IEnumerable<IAgentTool> tools) => tools.Select(t => new { Tool = t, Definition = Restrict(t.Definition) })
            .Where(t => t.Definition != null).Select(t => (IAgentTool)new RestrictedTool(t.Tool, t.Definition!)).ToArray();
        public void Limit(AgentRunOptions options)
        {
            if (ReadOnly) options.ExecutionMode = AgentExecutionMode.ReadOnly;
            options.ModelRequestLimit = Math.Min(options.ModelRequestLimit, Steps);
            options.ToolCallLimit = Math.Min(options.ToolCallLimit, ToolLimit);
            options.TimeoutSeconds = Math.Min(options.TimeoutSeconds, TimeoutSeconds);
        }
        private sealed class RestrictedTool : IAgentTool
        {
            private readonly IAgentTool _inner;
            public ToolDefinition Definition { get; }
            public RestrictedTool(IAgentTool inner, ToolDefinition definition) { _inner = inner; Definition = definition; }
            public Task<ToolResult> ExecuteAsync(ToolContext context, string argumentsJson, CancellationToken cancellationToken) => _inner.ExecuteAsync(context, argumentsJson, cancellationToken);
        }
    }
}
