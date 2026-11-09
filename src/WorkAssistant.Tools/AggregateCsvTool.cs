using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>
    /// aggregate_csv：确定性按部门汇总金额。所有数字由 decimal 计算产生，
    /// 模型只拿到计算结果，不参与算术。
    /// </summary>
    public sealed class AggregateCsvTool : AgentToolBase
    {
        public const int MaxPaths = 100;
        public const int MaxPreviewGroups = 20;

        private readonly WorkspacePolicy _policy;

        public AggregateCsvTool(WorkspacePolicy policy) => _policy = policy;

        protected override string FallbackErrorCode => ErrorCodes.CsvInvalid;

        public override ToolDefinition Definition { get; } = new ToolDefinition(
            "aggregate_csv",
            "读取一个或多个 CSV 文件，按指定列分组并对金额列求和。金额由程序用精确小数计算，" +
            "返回聚合 ID 供后续导出使用。",
            Schema(("paths", "array", "CSV 文件的工作区相对路径数组（inputs/ 下）", true),
                   ("group_by", "string", "分组列名，例如 部门", true),
                   ("sum_column", "string", "求和金额列名，例如 销售额", true)));

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "paths", "group_by", "sum_column");
            var paths = ToolArgs.RequireStringArray(args, "paths", MaxPaths);
            var groupBy = ToolArgs.RequireString(args, "group_by");
            var sumColumn = ToolArgs.RequireString(args, "sum_column");

            long totalBytes = 0;
            var totalRows = 0;
            var sums = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var path in paths)
            {
                if (!seen.Add(path))
                {
                    throw new ToolException(ErrorCodes.InvalidArguments, "重复的文件路径：" + path);
                }
                cancellationToken.ThrowIfCancellationRequested();

                var full = _policy.ResolveReadablePath(path);
                var table = CsvReading.Read(full, path);
                totalBytes += table.FileBytes;
                if (totalBytes > CsvReading.MaxTotalBytes)
                {
                    throw new ToolException(ErrorCodes.LimitExceeded,
                        "本任务读取总量超过 50 MiB 限制。");
                }

                var groupIndex = Array.IndexOf(table.Header, groupBy);
                var sumIndex = Array.IndexOf(table.Header, sumColumn);
                if (groupIndex < 0 || sumIndex < 0)
                {
                    var missing = groupIndex < 0 ? groupBy : sumColumn;
                    throw new ToolException(ErrorCodes.CsvInvalid,
                        $"文件 {path} 缺少列：{missing}；实际列：{string.Join(",", table.Header)}。");
                }
                if (HasDuplicate(table.Header))
                {
                    throw new ToolException(ErrorCodes.CsvInvalid,
                        "表头存在重复列名：" + path);
                }

                for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                {
                    totalRows++;
                    if (totalRows > CsvReading.MaxDataRows)
                    {
                        throw new ToolException(ErrorCodes.LimitExceeded,
                            "本任务数据行总数超过 100000 限制。");
                    }
                    var row = table.Rows[rowIndex];
                    var group = row[groupIndex].Trim();
                    if (group.Length == 0)
                    {
                        throw new ToolException(ErrorCodes.CsvInvalid,
                            $"{path} 第 {rowIndex + 1} 条记录的 {groupBy} 为空。");
                    }
                    var amount = AmountParser.TryParse(row[sumIndex]);
                    if (amount == null)
                    {
                        throw new ToolException(ErrorCodes.CsvInvalid,
                            $"{path} 第 {rowIndex + 1} 条记录的 {sumColumn} 无效：" +
                            $"'{row[sumIndex]}'（只接受最多两位小数的数字，允许负数）。");
                    }
                    try
                    {
                        sums[group] = sums.TryGetValue(group, out var acc) ? checked(acc + amount.Value) : amount.Value;
                    }
                    catch (OverflowException)
                    {
                        throw new ToolException(ErrorCodes.LimitExceeded,
                            $"合计溢出：{path} 第 {rowIndex + 1} 条记录。");
                    }
                }
            }

            var sorted = new List<KeyValuePair<string, decimal>>(sums);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            decimal grandTotal = 0m;
            try
            {
                foreach (var kv in sorted)
                {
                    grandTotal = checked(grandTotal + kv.Value);
                }
            }
            catch (OverflowException)
            {
                throw new ToolException(ErrorCodes.LimitExceeded, "合计溢出。");
            }

            var result = new AggregationResult(
                "", groupBy, sumColumn, paths, totalRows, sorted, grandTotal);
            var aggregationId = context.Aggregations.Add(result);

            var preview = new JArray();
            foreach (var kv in sorted.Take(MaxPreviewGroups))
            {
                preview.Add(new JObject
                {
                    [groupBy] = kv.Key,
                    [sumColumn] = AmountParser.Format(kv.Value)
                });
            }

            var data = new Dictionary<string, object?>
            {
                ["aggregation_id"] = aggregationId,
                ["record_count"] = totalRows,
                ["group_count"] = sorted.Count,
                ["preview"] = preview,
                ["preview_truncated"] = sorted.Count > MaxPreviewGroups,
                ["total"] = AmountParser.Format(grandTotal)
            };
            return XpTask.FromResult(ToolResult.Success(data));
        }

        private static bool HasDuplicate(string[] header)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in header)
            {
                if (!set.Add(h)) return true;
            }
            return false;
        }
    }
}
