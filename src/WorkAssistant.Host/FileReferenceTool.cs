using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using WorkAssistant.Tools;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Host
{
    // External model arguments refer to the immutable task manifest, never physical paths.
    internal sealed class FileReferenceTool : IAgentTool
    {
        private readonly IAgentTool _inner;
        private readonly Dictionary<string, string> _paths;
        private readonly Dictionary<string, string> _ids;
        private readonly Dictionary<string, string> _hashes;
        private readonly string _snapshot;
        public ToolDefinition Definition { get; }
        public FileReferenceTool(IAgentTool inner, Dictionary<string, string> files, Dictionary<string, string> hashes, string snapshot)
        {
            _inner = inner; _paths = files; _hashes = hashes; _snapshot = snapshot; _ids = files.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);
            var definition = inner.Definition;
            var schema = (JObject)definition.Parameters.DeepClone();
            // A tool can itself have a parameter named "description" whose value is a schema object.
            // Only rewrite actual description strings; snapshot before replacing tokens.
            foreach (var description in schema.Descendants().OfType<JProperty>()
                .Where(p => p.Name == "description" && p.Value.Type == JTokenType.String).ToList())
                if (description.Parent?.Parent is JProperty field && new[] { "path", "paths", "template_path", "source" }.Contains(field.Name) &&
                    (string?)description.Value is string text && (text.Contains("文件") || text.Contains("路径")))
                    description.Value = text + "。现有输入文件必须传任务清单中的 file_id，不能传文件路径；整理的 target 是 inputs/ 下的新相对路径。";
            Definition = new ToolDefinition(definition.Name, definition.Description, schema, definition.ProducesArtifacts, definition.RequiresApproval) { PolicyRequiresApproval = definition.PolicyRequiresApproval };
        }
        private string Resolve(string id, List<IDisposable> locks)
        {
            if (!_paths.TryGetValue(id, out var path)) throw new ToolException(ErrorCodes.PathDenied, "未知 file_id；请先查看 list_files 和当前任务文件清单。");
            var stream = VerifiedFiles.OpenRead(Path.Combine(_snapshot, path), _snapshot);
            try
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                if (!_hashes.TryGetValue(id, out var expected) || hash != expected) throw new ToolException(ErrorCodes.PathDenied, "输入快照摘要已变化，请重新附加文件并新建任务。");
                locks.Add(stream); return path;
            }
            catch { stream.Dispose(); throw; }
        }
        public async Task<ToolResult> ExecuteAsync(ToolContext context, string json, CancellationToken token)
        {
            var locks = new List<IDisposable>();
            try
            {
                var args = ToolArgs.Parse(json, Definition.Name);
                foreach (var property in args.Descendants().OfType<JProperty>().ToList())
                {
                    bool fileProperty = (property.Parent == args && new[] { "path", "template_path" }.Contains(property.Name)) ||
                        (property.Name == "source" && property.Parent?.Parent?.Parent is JProperty owner && owner.Name == "operations");
                    if (fileProperty && property.Value.Type == JTokenType.String)
                        property.Value = Resolve((string)property.Value!, locks);
                    if (property.Parent == args && property.Name == "paths" && property.Value is JArray array)
                    {
                        if (array.Count > 100) throw new ToolException(ErrorCodes.LimitExceeded, "一次工具调用最多引用 100 个文件。");
                        for (int i = 0; i < array.Count; i++)
                        {
                            if (array[i]?.Type != JTokenType.String) throw new ToolException(ErrorCodes.InvalidArguments, "paths 每项必须为 file_id 字符串。");
                            array[i] = Resolve((string)array[i]!, locks);
                        }
                    }
                }
                var result = await _inner.ExecuteAsync(context, args.ToString(Formatting.None), token).ConfigureAwait(false);
                var data = (JObject)JObject.Parse(ToolResultJson.Serialize(result))["data"]!;
                if (Definition.Name == "list_files" && data["file_details"] is JArray details)
                    foreach (var detail in details.OfType<JObject>()) detail["name"] = Path.GetFileName((string?)detail["path"] ?? "");
                foreach (var value in data.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String).ToList())
                {
                    // Root tools may receive new IDs after a child-artifact handoff.
                    var entry = _paths.FirstOrDefault(p => string.Equals(p.Value, (string)value!, StringComparison.OrdinalIgnoreCase));
                    if (entry.Key != null) value.Value = entry.Key;
                }
                return new ToolResult(result.Ok, data.Properties().ToDictionary(p => p.Name, p => (object?)p.Value), result.Error, result.Artifacts);
            }
            catch (ToolException ex) { return ToolResult.Failure(ex.Code, ex.Message); }
            catch (JsonException) { return ToolResult.Failure(ErrorCodes.InvalidArguments, "工具参数不是合法 JSON 对象。"); }
            catch (IOException) { return ToolResult.Failure(ErrorCodes.PathDenied, "输入快照无法读取或已被占用，请重新附加文件并新建任务。"); }
            catch (UnauthorizedAccessException) { return ToolResult.Failure(ErrorCodes.PathDenied, "输入快照访问被拒绝。"); }
            finally { foreach (var handle in locks) handle.Dispose(); }
        }
    }
}
