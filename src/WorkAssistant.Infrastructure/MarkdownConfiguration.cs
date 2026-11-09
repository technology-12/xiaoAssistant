using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    // Deliberately bounded frontmatter subset, not a general YAML interpreter.
    public sealed class MarkdownConfiguration
    {
        public JObject Metadata { get; private set; } = new();
        public string Body { get; private set; } = "";
        public static MarkdownConfiguration Parse(string text)
        {
            text = text.TrimStart('\ufeff').Replace("\r\n", "\n");
            if (!text.StartsWith("---\n", StringComparison.Ordinal)) throw Invalid("Missing frontmatter");
            int end = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (end < 0 || end > 16384) throw Invalid("Missing or oversized frontmatter");
            string header = text.Substring(4, end - 4);
            JObject metadata;
            try
            {
                if (header.TrimStart().StartsWith("{", StringComparison.Ordinal))
                {
                    using var reader = new JsonTextReader(new StringReader(header)) { MaxDepth = 8 };
                    metadata = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw Invalid("Trailing frontmatter content");
                }
                else
                {
                    var lines = header.Split('\n'); if (lines.Length > 200) throw Invalid("Too many frontmatter lines");
                    int index = 0; metadata = Map(lines, ref index, 0);
                    if (index != lines.Length) throw Invalid("Unexpected indentation");
                }
            }
            catch (JsonException) { throw Invalid("Invalid frontmatter JSON or scalar"); }
            return new MarkdownConfiguration { Metadata = metadata, Body = text.Substring(end + 5).Trim() };
        }
        private static JObject Map(string[] lines, ref int index, int indent)
        {
            if (indent > 8) throw Invalid("Frontmatter nesting exceeds limit");
            var result = new JObject();
            while (index < lines.Length)
            {
                string line = lines[index];
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal)) { index++; continue; }
                int spaces = line.TakeWhile(c => c == ' ').Count();
                if (spaces < indent) break;
                if (spaces != indent || line.Contains('\t')) throw Invalid("Use two-space indentation");
                var match = Regex.Match(line.Substring(indent), "^([a-zA-Z_][a-zA-Z0-9_-]*|\\*|'\\*'|\"\\*\"):(?: +(.*))?$");
                if (!match.Success) throw Invalid("Unsupported frontmatter syntax");
                string key = match.Groups[1].Value.Trim('\'', '"'), value = match.Groups[2].Value.Trim(); index++;
                if (result.Property(key) != null) throw Invalid("Duplicate frontmatter field: " + key);
                if (value == "|" || value == ">")
                {
                    var body = new List<string>();
                    while (index < lines.Length && (string.IsNullOrWhiteSpace(lines[index]) || lines[index].StartsWith(new string(' ', indent + 2), StringComparison.Ordinal)))
                        body.Add(lines[index++].Length >= indent + 2 ? lines[index - 1].Substring(indent + 2) : "");
                    result[key] = string.Join(value == ">" ? " " : "\n", body).Trim();
                }
                else if (value.Length > 0) result[key] = Scalar(value);
                else
                {
                    while (index < lines.Length && (string.IsNullOrWhiteSpace(lines[index]) || lines[index].TrimStart().StartsWith("#", StringComparison.Ordinal))) index++;
                    if (index >= lines.Length || !lines[index].StartsWith(new string(' ', indent + 2), StringComparison.Ordinal)) throw Invalid("Empty field: " + key);
                    if (lines[index].Substring(indent + 2).StartsWith("- ", StringComparison.Ordinal))
                    {
                        var list = new JArray();
                        while (index < lines.Length && lines[index].StartsWith(new string(' ', indent + 2) + "- ", StringComparison.Ordinal))
                            list.Add(Scalar(lines[index++].Substring(indent + 4).Trim()));
                        result[key] = list;
                    }
                    else result[key] = Map(lines, ref index, indent + 2);
                }
            }
            return result;
        }
        private static JToken Scalar(string value)
        {
            if (value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal))
            {
                // Native YAML flow lists accept unquoted tool names; quoted commas require JSON syntax.
                if (value.Contains('"')) return JArray.Parse(value);
                return new JArray(value.Substring(1, value.Length - 2).Split(',').Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => Scalar(v.Trim())));
            }
            if (value.StartsWith("\"", StringComparison.Ordinal)) return JToken.Parse(value);
            if (value.StartsWith("'", StringComparison.Ordinal))
            {
                if (!value.EndsWith("'", StringComparison.Ordinal) || value.Length < 2) throw Invalid("Unclosed scalar");
                return new JValue(value.Substring(1, value.Length - 2).Replace("''", "'"));
            }
            if (value == "true" || value == "false") return new JValue(value == "true");
            if (Regex.IsMatch(value, @"^-?\d+$"))
            { if (!int.TryParse(value, out int number)) throw Invalid("Integer out of range"); return new JValue(number); }
            if (value.Length == 0 || "&*!{[".Contains(value[0]) || value.Contains(": ") || value.Contains(" #")) throw Invalid("Quote this scalar; aliases, tags and inline comments are unsupported");
            return new JValue(value);
        }
        public static void Fields(JObject obj, params string[] names)
        { foreach (var p in obj.Properties()) if (!names.Contains(p.Name)) throw Invalid("Unsupported field: " + p.Name); }
        public static string Text(JObject obj, string name, string fallback = "")
        {
            if (obj[name] == null) return fallback;
            if (obj[name]!.Type != JTokenType.String) throw Invalid(name + " must be a string");
            return (string)obj[name]!;
        }
        public static bool Flag(JObject obj, string name)
        { if (obj[name] == null) return false; if (obj[name]!.Type != JTokenType.Boolean) throw Invalid(name + " must be boolean"); return (bool)obj[name]!; }
        public static int Limit(JObject obj, string name, int maximum)
        {
            if (obj[name] == null) return maximum;
            if (obj[name]!.Type != JTokenType.Integer || !int.TryParse(obj[name]!.ToString(), out int n) || n < 1 || n > maximum) throw Invalid(name + " is outside runtime limits");
            return n;
        }
        public static List<string> Tools(JToken value)
        {
            IEnumerable<JToken> items = value is JArray array ? array : value.Type == JTokenType.String
                ? ((string)value!).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => (JToken)new JValue(s)) : throw Invalid("tools must be a list");
            var result = items.Select(v => v.Type == JTokenType.String ? (string)v! : throw Invalid("Invalid tool name")).ToList();
            if (result.Any(v => !SkillRules.ToolNames.Contains(v)) || result.Distinct().Count() != result.Count) throw Invalid("Unknown or duplicate native tool name");
            return result;
        }
        public static ToolException Invalid(string message) => new(ErrorCodes.InvalidArguments, "项目配置：" + message);
    }
}
