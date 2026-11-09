using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>
    /// export_summary：把聚合结果写成 outputs/&lt;task_id&gt;/summary.csv 与 report.md。
    /// - 只接受 aggregation_id，金额与内容全部来自已验证的聚合结果，模型不能传入金额或文本。
    /// - 临时文件全部写好并校验后才提交；失败或取消删除临时文件。
    /// - 同一任务重复调用返回同一份成果，不重复登记。
    /// - 不覆盖已有不同内容；冲突返回明确错误。
    /// </summary>
    public sealed class ExportSummaryTool : AgentToolBase
    {
        public const string SummaryCsvName = "summary.csv";
        public const string ReportMdName = "report.md";

        private readonly WorkspacePolicy _policy;

        public ExportSummaryTool(WorkspacePolicy policy) => _policy = policy;

        protected override string FallbackErrorCode => ErrorCodes.OutputWriteFailed;

        public override ToolDefinition Definition { get; } = new ToolDefinition(
            "export_summary",
            "将聚合结果导出为 summary.csv 与 report.md 到任务输出目录，返回成果清单。" +
            "只能引用本任务 aggregate_csv/aggregate_table 返回的 aggregation_id。",
            Schema(("aggregation_id", "string", "aggregate_csv 返回的聚合 ID", true)),
            producesArtifacts: true);

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "aggregation_id");
            var aggregationId = ToolArgs.RequireString(args, "aggregation_id");

            var aggregation = context.Aggregations.Find(aggregationId);
            if (aggregation == null)
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "aggregation_id 无效或已过期（不能跨任务引用）。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var artifacts = TaskOutputs.CommitFiles(_policy, context.TaskId, new[] {
                (SummaryCsvName, BuildSummaryCsv(aggregation), "summary_csv"),
                (ReportMdName, BuildReportMarkdown(aggregation, context.TaskId), "report_md") },
                (path, type) => { if (type == "summary_csv") ExportValidators.AggregateTable(path, aggregation, false); });
            return XpTask.FromResult(ToolResult.Success(BuildData(aggregationId, aggregation, artifacts), artifacts));
        }

        // ---- 内容生成 -------------------------------------------------------

        /// <summary>UTF-8 BOM + 内容。</summary>
        private static byte[] Utf8WithBom(StringBuilder sb)
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            var preamble = encoding.GetPreamble();
            var body = encoding.GetBytes(sb.ToString());
            var result = new byte[preamble.Length + body.Length];
            System.Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
            System.Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
            return result;
        }

        internal static byte[] BuildSummaryCsv(AggregationResult aggregation)
        {
            var sb = new StringBuilder();
            sb.Append(EscapeCsvHeader(aggregation.GroupByColumn)).Append(',')
              .Append(EscapeCsvHeader(aggregation.SumColumn)).Append("\r\n");
            foreach (var kv in aggregation.Groups)
            {
                sb.Append(EscapeCsvField(OutputSanitizer.CsvCell(kv.Key))).Append(',')
                  .Append(AmountParser.Format(kv.Value)).Append("\r\n");
            }
            return Utf8WithBom(sb);
        }

        internal static byte[] BuildReportMarkdown(AggregationResult aggregation, string taskId)
        {
            var sb = new StringBuilder();
            sb.Append("# 数据汇总报告\n\n");
            sb.Append("- 任务 ID：`").Append(taskId).Append("`\n");
            sb.Append("- 生成时间（UTC）：").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")).Append("\n");
            sb.Append("- 来源文件（").Append(aggregation.SourcePaths.Count).Append(" 个）：\n");
            foreach (var p in aggregation.SourcePaths)
            {
                sb.Append("  - `").Append(OutputSanitizer.MarkdownCell(p)).Append("`\n");
            }
            sb.Append("\n## 数据口径\n\n");
            if (aggregation.FilterDescription.Length > 0)
            {
                sb.Append("- ").Append(OutputSanitizer.MarkdownCell(aggregation.FilterDescription)).Append('\n');
            }
            if (aggregation.MappingDescription.Length > 0)
            {
                sb.Append("- ").Append(OutputSanitizer.MarkdownCell(aggregation.MappingDescription)).Append('\n');
            }
            if (aggregation.FilterDescription.Length == 0 && aggregation.MappingDescription.Length == 0)
            {
                sb.Append("- 未应用筛选与分组映射（全量汇总）\n");
            }
            sb.Append('\n');
            sb.Append("## 部门汇总\n\n");
            sb.Append("| ").Append(OutputSanitizer.MarkdownCell(aggregation.GroupByColumn))
              .Append(" | ").Append(OutputSanitizer.MarkdownCell(aggregation.SumColumn)).Append(" |\n");
            sb.Append("|---|---:|\n");
            foreach (var kv in aggregation.Groups)
            {
                sb.Append("| ").Append(OutputSanitizer.MarkdownCell(kv.Key))
                  .Append(" | ").Append(AmountParser.Format(kv.Value)).Append(" |\n");
            }
            sb.Append("\n- 数据记录数：").Append(aggregation.RecordCount).Append("\n");
            sb.Append("- 部门数：").Append(aggregation.Groups.Count).Append("\n");
            sb.Append("- 合计金额：").Append(AmountParser.Format(aggregation.Total)).Append(" 元\n\n");
            sb.Append("## 校验说明\n\n");
            sb.Append("- 所有金额由程序使用精确十进制（decimal）按 invariant 格式计算，未使用模型心算结果。\n");
            sb.Append("- 输入文件保持只读，未修改源数据。\n");
            sb.Append("- summary.csv 为 UTF-8 BOM 编码、CRLF 换行，金额固定两位小数。\n");
            return Utf8WithBom(sb);
        }

        private static Dictionary<string, object?> BuildData(string aggregationId,
            AggregationResult aggregation, IList<ArtifactInfo> artifacts)
        {
            var list = new List<string>();
            foreach (var a in artifacts) list.Add(a.Path);
            return new Dictionary<string, object?>
            {
                ["aggregation_id"] = aggregationId,
                ["record_count"] = aggregation.RecordCount,
                ["group_count"] = aggregation.Groups.Count,
                ["total"] = AmountParser.Format(aggregation.Total),
                ["artifacts"] = list
            };
        }

        // ---- 文件提交 -------------------------------------------------------

        private static string EscapeCsvHeader(string header) => EscapeCsvField(OutputSanitizer.CsvCell(header));

        private static string EscapeCsvField(string field)
        {
            var needsQuote = field.Contains(",") || field.Contains("\"") ||
                             field.Contains("\r") || field.Contains("\n");
            if (!needsQuote) return field;
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        private static void WriteAllBytesKeepExisting(string path, byte[] content)
        {
            if (File.Exists(path))
            {
                throw new ToolException(ErrorCodes.OutputWriteFailed,
                    "临时文件已存在，拒绝覆盖：" + Path.GetFileName(path));
            }
            File.WriteAllBytes(path, content);
        }

        private static void Commit(string tempPath, string finalPath)
        {
            if (File.Exists(finalPath))
            {
                // 目标已存在：内容一致则幂等成功，否则冲突报错。
                if (ByteArrayEquals(File.ReadAllBytes(finalPath), File.ReadAllBytes(tempPath)))
                {
                    TryDelete(tempPath);
                    return;
                }
                throw new ToolException(ErrorCodes.OutputWriteFailed,
                    "目标文件已存在且内容不同，拒绝覆盖：" + Path.GetFileName(finalPath));
            }
            File.Move(tempPath, finalPath);
        }

        private static IList<ArtifactInfo>? ReadArtifacts(
            WorkspacePolicy policy, string summaryCsv, string reportMd)
        {
            try
            {
                var list = new List<ArtifactInfo>
                {
                    MakeArtifact(policy, summaryCsv, "summary_csv"),
                    MakeArtifact(policy, reportMd, "report_md")
                };
                return list;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static ArtifactInfo MakeArtifact(WorkspacePolicy policy, string fullPath, string type)
        {
            var info = new FileInfo(fullPath);
            return new ArtifactInfo(
                policy.ToWorkspaceRelative(fullPath), type, info.Length, Sha256File(fullPath));
        }

        private static string Sha256File(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            var hash = sha.ComputeHash(stream);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static bool ByteArrayEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // 清理失败不掩盖原始异常。
            }
        }
    }
}
