using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>list_files：列出 inputs（或其子目录）中的文件，最多 100 个。只读。</summary>
    public sealed class ListFilesTool : AgentToolBase
    {
        public const int MaxFiles = 100;
        /// <summary>1.0 支持的输入扩展名（白名单；新增格式须同步工具与 README）。</summary>
        public static readonly string[] SupportedExtensions =
            { ".csv", ".xlsx", ".docx", ".pptx", ".pdf", ".txt", ".md", ".json", ".log", ".xml" };

        private readonly WorkspacePolicy _policy;

        public ListFilesTool(WorkspacePolicy policy) => _policy = policy;

        protected override string FallbackErrorCode => ErrorCodes.CsvInvalid;

        public override ToolDefinition Definition { get; } = new ToolDefinition(
            "list_files",
            "列出授权输入目录中的文件（默认 CSV；可用 extensions 指定 .csv/.xlsx/.docx/.pptx/.pdf），" +
            "返回工作区相对路径与文件大小。",
            Schema(("directory", "string",
                "要列出的目录，必须是 inputs 或其子目录（如 inputs 或 inputs/2024）", true),
                   ("extensions", "array", "扩展名数组（可选，默认 [\".csv\"]），允许 .csv/.xlsx/.docx/.pptx/.pdf/.txt/.md/.json/.log/.xml", false)));

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "directory", "extensions");
            var directory = ToolArgs.RequireString(args, "directory");
            var extensions = ToolArgs.OptionalStringArray(args, "extensions", 8);

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csv" };
            if (extensions != null)
            {
                allowed.Clear();
                foreach (var ext in extensions)
                {
                    var normalized = ext.StartsWith(".", StringComparison.Ordinal) ? ext : "." + ext;
                    if (Array.IndexOf(SupportedExtensions,
                            normalized.ToLowerInvariant()) < 0)
                    {
                        throw new ToolException(ErrorCodes.InvalidArguments,
                            "不支持的扩展名：" + ext + "；允许：" +
                            string.Join("/", SupportedExtensions));
                    }
                    allowed.Add(normalized);
                }
            }

            var fullDir = _policy.ResolveReadableDirectory(directory);
            var files = new List<JObject>();
            foreach (var file in Directory.EnumerateFiles(fullDir, "*.*", SearchOption.AllDirectories)
                         .Where(f => allowed.Contains(Path.GetExtension(f)))
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                         .Take(MaxFiles + 1))
            {
                // 再次逐个解析，确保每个文件都在 inputs 边界内且不经过重解析点。
                var rel = _policy.ToWorkspaceRelative(file);
                _policy.ResolveReadablePath(rel);
                files.Add(new JObject
                {
                    ["path"] = rel,
                    ["bytes"] = new FileInfo(file).Length
                });
            }

            var truncated = false;
            if (files.Count > MaxFiles)
            {
                files.RemoveAt(files.Count - 1);
                truncated = true;
            }

            var data = new Dictionary<string, object?>
            {
                ["files"] = files.Select(f => (string)f["path"]!).ToList(),
                ["file_details"] = files,
                ["count"] = files.Count,
                ["truncated"] = truncated
            };
            return XpTask.FromResult(ToolResult.Success(data));
        }
    }
}
