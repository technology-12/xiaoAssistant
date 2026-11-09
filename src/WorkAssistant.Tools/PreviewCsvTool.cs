using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>preview_csv：返回表头、最多 20 行预览与检测到的编码。只读。</summary>
    public sealed class PreviewCsvTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;

        public PreviewCsvTool(WorkspacePolicy policy) => _policy = policy;

        protected override string FallbackErrorCode => ErrorCodes.CsvInvalid;

        public override ToolDefinition Definition { get; } = new ToolDefinition(
            "preview_csv",
            "预览 CSV 文件：返回列名、最多 max_rows 行数据预览和检测到的编码。",
            Schema(("path", "string", "CSV 文件的工作区相对路径（inputs/ 下）", true),
                   ("max_rows", "integer", "预览行数，范围 1~20", true)));

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "path", "max_rows");
            var path = ToolArgs.RequireString(args, "path");
            var maxRows = ToolArgs.RequireIntInRange(args, "max_rows", 1, 20);

            var full = _policy.ResolveReadablePath(path);
            var table = CsvReading.Read(full, path);

            var preview = new JArray();
            foreach (var row in table.Rows)
            {
                if (preview.Count >= maxRows) break;
                var obj = new JObject();
                for (var i = 0; i < table.Header.Length; i++)
                {
                    obj[table.Header[i]] = row[i];
                }
                preview.Add(obj);
            }

            var data = new Dictionary<string, object?>
            {
                ["path"] = path,
                ["columns"] = new List<string>(table.Header),
                ["total_data_rows"] = table.Rows.Count,
                ["encoding"] = table.EncodingName,
                ["preview_rows"] = preview
            };
            return XpTask.FromResult(ToolResult.Success(data));
        }
    }
}
