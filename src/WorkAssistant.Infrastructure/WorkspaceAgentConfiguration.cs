using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    public sealed class ProjectCommand
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Agent { get; set; } = "general";
        public string Model { get; set; } = "inherit";
        public string Template { get; set; } = "";
    }
    public sealed class ProjectInvocation
    {
        public AgentRole Role { get; set; } = null!;
        public string Prompt { get; set; } = "";
        public string CommandName { get; set; } = "";
        public string ModelAlias { get; set; } = "inherit";
    }
    public sealed class WorkspaceAgentConfiguration
    {
        public IList<AgentRole> Roles { get; }
        public IList<SkillDefinition> Skills { get; }
        public IList<ProjectCommand> Commands { get; }
        public string Instructions { get; }
        public string Hash { get; }
        public string Snapshot { get; }
        public WorkspaceAgentConfiguration(string workspace)
        {
            string source = workspace;
            try
            {
            var files = new ProjectConfigurationFiles(workspace);
            var sources = new JObject();
            string? Read(string path) { source = path; var text = files.Read(path); if (text != null) sources[path.Replace('\\', '/')] = text; return text; }
            string Body(string path, out JObject metadata)
            {
                var parsed = MarkdownConfiguration.Parse(Read(path) ?? throw MarkdownConfiguration.Invalid("Missing " + path));
                metadata = parsed.Metadata;
                if (string.IsNullOrWhiteSpace(parsed.Body) || parsed.Body.Length > 30000) throw MarkdownConfiguration.Invalid("Empty or oversized instruction body: " + path);
                return parsed.Body;
            }
            var roles = new List<AgentRole> {
                new() { Name = "general", Description = "通用分析与办公成果生成" },
                new() { Name = "explore", Description = "只读检索、阅读与探索", Mode = "subagent", ReadOnly = true },
                new() { Name = "review", Description = "只读审查与核对", Mode = "subagent", ReadOnly = true },
                new() { Name = "plan", Description = "只读规划", Mode = "primary", ReadOnly = true }
            };
            foreach (var folder in new[] { ".claude/agents", ".claude/agent" })
            foreach (var path in files.Enumerate(folder, false))
            {
                string name = Path.GetFileNameWithoutExtension(path); ValidateName(name);
                string body = Body(path, out var meta);
                MarkdownConfiguration.Fields(meta, "name", "description", "mode", "model", "tools", "permission", "hidden", "disable", "read_only", "steps", "tool_limit", "timeout_seconds");
                if (MarkdownConfiguration.Text(meta, "name", name) != name || roles.Any(r => r.Name == name)) throw MarkdownConfiguration.Invalid("Duplicate or mismatched agent name: " + name);
                var role = new AgentRole { Name = name, Instructions = body, Description = MarkdownConfiguration.Text(meta, "description", name),
                    Mode = MarkdownConfiguration.Text(meta, "mode", "subagent"), ModelAlias = Model(meta, "model"), ReadOnly = MarkdownConfiguration.Flag(meta, "read_only"),
                    Hidden = MarkdownConfiguration.Flag(meta, "hidden"), Disabled = MarkdownConfiguration.Flag(meta, "disable"),
                    Steps = MarkdownConfiguration.Limit(meta, "steps", 24), ToolLimit = MarkdownConfiguration.Limit(meta, "tool_limit", 48), TimeoutSeconds = MarkdownConfiguration.Limit(meta, "timeout_seconds", 600) };
                if (!new[] { "all", "primary", "subagent" }.Contains(role.Mode) || role.Description.Length > 500) throw MarkdownConfiguration.Invalid("Invalid agent mode/description");
                if (meta["tools"] is JObject map)
                {
                    role.Tools = new List<string>();
                    foreach (var p in map.Properties())
                    {
                        if (!SkillRules.ToolNames.Contains(p.Name) || p.Value.Type != JTokenType.Boolean) throw MarkdownConfiguration.Invalid("Invalid tool switch");
                        if ((bool)p.Value) role.Tools.Add(p.Name);
                    }
                }
                else if (meta["tools"] != null) role.Tools = MarkdownConfiguration.Tools(meta["tools"]!);
                if (meta["permission"] is JObject rules)
                    foreach (var p in rules.Properties()) { ValidatePermission(p.Name, p.Value); role.Permissions.Add(p.Name, (string)p.Value!); }
                else if (meta["permission"] != null) { ValidatePermission("*", meta["permission"]!); role.Permissions.Add("*", (string)meta["permission"]!); }
                roles.Add(role);
            }
            var skills = new List<SkillDefinition>();
            foreach (var directory in files.Enumerate(".claude/skills", true))
            {
                string name = Path.GetFileName(directory); ValidateName(name);
                string path = Path.Combine(directory, "SKILL.md"); if (Read(path) is not string skillText) continue;
                var parsed = MarkdownConfiguration.Parse(skillText); var meta = parsed.Metadata;
                MarkdownConfiguration.Fields(meta, "name", "description", "allowed-tools", "version");
                if (MarkdownConfiguration.Text(meta, "name") != name) throw MarkdownConfiguration.Invalid("Skill name must match directory");
                var skill = new SkillDefinition { Id = "project-" + name, Name = name, ProjectLocal = true, Description = MarkdownConfiguration.Text(meta, "description"),
                    Version = MarkdownConfiguration.Text(meta, "version", "1.0"), Instructions = parsed.Body,
                    AllowedTools = meta["allowed-tools"] == null ? SkillRules.ToolNames.ToList() : MarkdownConfiguration.Tools(meta["allowed-tools"]!) };
                SkillRules.Validate(skill); skills.Add(skill);
            }
            var commands = new List<ProjectCommand>();
            foreach (var path in files.Enumerate(".claude/commands", false))
            {
                string name = Path.GetFileNameWithoutExtension(path); ValidateName(name);
                if (name == "agent") throw MarkdownConfiguration.Invalid("Command name agent is reserved");
                string body = Body(path, out var meta); MarkdownConfiguration.Fields(meta, "description", "agent", "model");
                var command = new ProjectCommand { Name = name, Template = body, Description = MarkdownConfiguration.Text(meta, "description", name),
                    Agent = MarkdownConfiguration.Text(meta, "agent", "general"), Model = Model(meta, "model") };
                if (commands.Any(c => c.Name == name)) throw MarkdownConfiguration.Invalid("Duplicate command");
                var role = roles.SingleOrDefault(r => r.Name == command.Agent && !r.Disabled && r.Mode != "subagent");
                if (role == null) throw MarkdownConfiguration.Invalid("Command requires an enabled primary/all agent");
                commands.Add(command);
            }
            Instructions = Read("AGENTS.md") ?? Read("CLAUDE.md") ?? "";
            Roles = roles.AsReadOnly(); Skills = skills.AsReadOnly(); Commands = commands.AsReadOnly();
            Snapshot = new JObject { ["version"] = 1, ["sources"] = sources }.ToString(Newtonsoft.Json.Formatting.None);
            using var sha = SHA256.Create(); Hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Snapshot))).Replace("-", "").ToLowerInvariant();
            }
            catch (ToolException ex) { throw new ToolException(ex.Code, source + ": " + ex.Message); }
        }
        public AgentRole Role(string name, bool child)
        {
            var role = Roles.SingleOrDefault(r => r.Name == name && !r.Disabled && (child ? r.Mode != "primary" : r.Mode != "subagent"));
            return role ?? throw MarkdownConfiguration.Invalid("Unknown, disabled or incompatible agent role: " + name);
        }
        public ProjectInvocation Resolve(string input)
        {
            var result = new ProjectInvocation { Role = Role("general", false), Prompt = input };
            if (!input.StartsWith("/", StringComparison.Ordinal)) return result;
            int split = input.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            string command = input.Substring(1, split < 0 ? input.Length - 1 : split - 1);
            string arguments = split < 0 ? "" : input.Substring(split).TrimStart();
            if (command == "agent")
            {
                int end = arguments.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
                if (end < 1) throw MarkdownConfiguration.Invalid("Use /agent <role> <request>");
                result.Role = Role(arguments.Substring(0, end), false); result.Prompt = arguments.Substring(end).TrimStart();
            }
            else
            {
                var definition = Commands.SingleOrDefault(c => c.Name == command) ?? throw MarkdownConfiguration.Invalid("Unknown slash command: " + command);
                result.Role = Role(definition.Agent, false); result.CommandName = command;
                result.Prompt = definition.Template.Replace("$ARGUMENTS", arguments); result.ModelAlias = definition.Model;
                if (!definition.Template.Contains("$ARGUMENTS") && arguments.Length > 0) result.Prompt += "\n\n" + arguments;
            }
            if (string.IsNullOrWhiteSpace(result.Prompt) || result.Prompt.Length > 30000) throw MarkdownConfiguration.Invalid("Empty or oversized expanded request");
            return result;
        }
        private static void ValidateName(string name)
        { if (!Regex.IsMatch(name, "^[a-z][a-z0-9-]{1,39}$")) throw MarkdownConfiguration.Invalid("Use a 2-40 character lowercase name: " + name); }
        private static string Model(JObject meta, string key)
        {
            string value = MarkdownConfiguration.Text(meta, key, "inherit");
            if (!new[] { "inherit", "main", "worker" }.Contains(value)) throw MarkdownConfiguration.Invalid("Only configured main/worker model aliases are supported");
            return value;
        }
        private static void ValidatePermission(string name, JToken value)
        {
            if (name != "*" && !SkillRules.ToolNames.Contains(name) || value.Type != JTokenType.String || !new[] { "allow", "ask", "deny" }.Contains((string?)value))
                throw MarkdownConfiguration.Invalid("Permission needs a native tool name/* and allow/ask/deny");
        }
    }
}
