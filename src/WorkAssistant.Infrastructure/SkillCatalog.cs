using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    public sealed class SkillCatalog
    {
        private readonly string _builtins, _user;
        private readonly string? _workspace;
        public SkillCatalog(string workspace) : this() { _workspace = workspace; }
        public SkillCatalog()
        {
            _builtins = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "skills");
            _user = Path.Combine(ApplicationDataPaths.Root, "skills");
        }
        public List<string> LoadErrors { get; } = new();
        public List<SkillDefinition> Load()
        {
            LoadErrors.Clear(); var skills = new List<SkillDefinition>();
            foreach (var root in new[] { _builtins, _user })
            {
                if (!Directory.Exists(root)) continue;
                foreach (string directory in Directory.GetDirectories(root).OrderBy(p => p, StringComparer.Ordinal))
                {
                    if (Path.GetFileName(directory).StartsWith(".", StringComparison.Ordinal)) continue;
                    try
                    {
                        var skill = Read(Path.Combine(directory, "skill.json"), Path.Combine(directory, "SKILL.md"));
                        skill.BuiltIn = root == _builtins;
                        if (skills.Any(s => s.Id == skill.Id)) throw new IOException("技能 ID 重复，内置技能不会被覆盖。");
                        skills.Add(skill);
                    }
                    catch (Exception ex) when (ex is IOException || ex is ToolException || ex is JsonException || ex is UnauthorizedAccessException || ex is System.Text.DecoderFallbackException)
                    { LoadErrors.Add(Path.GetFileName(directory) + "：" + ex.Message); }
                }
            }
            if (!string.IsNullOrWhiteSpace(_workspace))
            {
                try
                {
                    foreach (var skill in new WorkspaceAgentConfiguration(_workspace!).Skills)
                    {
                        if (skills.Any(s => s.Id == skill.Id)) throw new IOException("项目技能 ID 与已有技能重复。");
                        skills.Add(skill);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is ToolException || ex is UnauthorizedAccessException)
                { LoadErrors.Add("项目技能：" + ex.Message); }
            }
            return skills.OrderBy(s => s.Id == "skill-creator" ? 0 : 1).ThenBy(s => s.Name).ToList();
        }
        public SkillDefinition? Find(string id) => Load().FirstOrDefault(s => s.Id == id);
        private static SkillDefinition Read(string jsonPath, string markdownPath)
        {
            if (new FileInfo(jsonPath).Length > 16000 || new FileInfo(markdownPath).Length > 128000) throw new IOException("技能文件超出大小限制。");
            var skill = JsonConvert.DeserializeObject<SkillDefinition>(File.ReadAllText(jsonPath)) ?? throw new IOException("技能元数据缺失。");
            string text = File.ReadAllText(markdownPath).Replace("\r\n", "\n");
            string header = SkillRules.Header(skill);
            if (!text.StartsWith(header, StringComparison.Ordinal)) throw new IOException("SKILL.md 的 name/description 必须与 skill.json 一致。");
            skill.Instructions = text.Substring(header.Length).Trim(); SkillRules.Validate(skill);
            var resources = new ProjectConfigurationFiles(Path.GetDirectoryName(markdownPath)!);
            var references = resources.Enumerate("references", false);
            if (references.Length > 24) throw new IOException("技能参考资料超过 24 份。");
            foreach (var reference in references)
            {
                string content = resources.Read(reference) ?? throw new IOException("技能参考资料缺失。");
                if (content.Length > 24000) throw new IOException("技能单份参考资料超过 24000 字符。");
                skill.Resources.Add(reference.Replace('\\', '/'), content);
            }
            return skill;
        }
        public void SaveNew(SkillDefinition skill)
        {
            SkillRules.Validate(skill);
            if (Find(skill.Id) != null) throw new IOException("技能 ID 已存在，请使用新的 ID；不会覆盖已有技能。");
            Directory.CreateDirectory(_user);
            string staging = Path.Combine(_user, ".draft-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(staging);
            DurableFiles.WriteText(Path.Combine(staging, "SKILL.md"), SkillRules.Markdown(skill));
            DurableFiles.WriteText(Path.Combine(staging, "skill.json"), JsonConvert.SerializeObject(skill, Formatting.Indented));
            Directory.Move(staging, Path.Combine(_user, skill.Id));
        }
        public SkillDefinition ImportDraft(string jsonPath)
        {
            if (!jsonPath.EndsWith(".skill.json", StringComparison.OrdinalIgnoreCase)) throw new IOException("请选择工具生成的 .skill.json 草稿。");
            string md = jsonPath.Substring(0, jsonPath.Length - ".skill.json".Length) + ".SKILL.md";
            return Read(jsonPath, md);
        }
    }
}
