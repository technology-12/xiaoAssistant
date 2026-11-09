using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace WorkAssistant.Core
{
    public sealed class SkillDefinition
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Version { get; set; } = "1.0";
        public List<string> AllowedTools { get; set; } = new();
        [JsonIgnore] public string Instructions { get; set; } = "";
        [JsonIgnore] public bool BuiltIn { get; set; }
        [JsonIgnore] public bool ProjectLocal { get; set; }
        [JsonIgnore] public Dictionary<string, string> Resources { get; set; } = new(StringComparer.Ordinal);
    }
    public static class SkillRules
    {
        public static readonly string[] ToolNames = { "list_files", "preview_csv", "preview_table", "aggregate_csv", "aggregate_table",
            "export_summary", "export_xlsx", "generate_docx", "fill_docx_template", "preview_docx_fields", "extract_pdf_text",
            "plan_file_organization", "apply_file_plan", "ask_user", "draft_skill", "read_document_text", "search_input_text",
            "update_task_plan", "read_task_history", "list_skills", "read_skill", "delegate_tasks", "import_task_artifacts", "list_project_commands", "plan_text_edit", "validate_text_edit", "apply_text_edit", "rollback_text_edit",
            "inspect_office", "write_word", "edit_word", "write_presentation", "edit_presentation", "document_workspace" };
        public static void Validate(SkillDefinition skill)
        {
            if (!Regex.IsMatch(skill.Id ?? "", "^[a-z][a-z0-9-]{1,47}$") || string.IsNullOrWhiteSpace(skill.Name) || skill.Name.Length > 60 ||
                string.IsNullOrWhiteSpace(skill.Description) || skill.Description.Length > 500 || string.IsNullOrWhiteSpace(skill.Instructions) || skill.Instructions.Length > 30000 ||
                !System.Version.TryParse(skill.Version, out _) || skill.AllowedTools == null || skill.AllowedTools.Count == 0 || skill.AllowedTools.Count > ToolNames.Length ||
                skill.AllowedTools.Any(t => !ToolNames.Contains(t)) || skill.AllowedTools.Distinct().Count() != skill.AllowedTools.Count)
                throw new ToolException(ErrorCodes.InvalidArguments, "技能格式无效：请检查 ID、名称、描述、版本、说明和内置工具列表。");
        }
        public static string Header(SkillDefinition skill) => "---\nname: " + skill.Id + "\ndescription: " + JsonConvert.SerializeObject(skill.Description) + "\n---\n";
        public static string Markdown(SkillDefinition skill) { Validate(skill); return Header(skill) + "\n" + skill.Instructions.Trim() + "\n"; }
    }
}
