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
    /// aggregate_table（正式版 §4.1：合并、筛选、映射列、分组汇总）：
    /// - CSV 与 XLSX 同一管线；金额 decimal 精确计算，模型不参与算术。
    /// - where 筛选与 group_mapping 映射均为确定性代码路径，口径写入结果描述，
    ///   供报告与后续轮次追溯（§7.3：口径不得被摘要替代）。
    /// </summary>
    public sealed class AggregateTableTool : AgentToolBase
    {
        public const int MaxPaths = 100;
        public const int MaxPreviewGroups = 20;
        public const int MaxMappingEntries = 200;

        private readonly WorkspacePolicy _policy;

        public AggregateTableTool(WorkspacePolicy policy) => _policy = policy;

        protected override string FallbackErrorCode => ErrorCodes.CsvInvalid;

        public override ToolDefinition Definition { get; } = new ToolDefinition(
            "aggregate_table",
            "读取一个或多个 CSV/XLSX 表格，可选按列筛选（where）与分组值映射（group_mapping），" +
            "再按指定列分组并对金额列求和。金额由程序用精确小数计算，返回聚合 ID 供导出使用。",
            Schema(("paths", "array", "表格文件的工作区相对路径数组（inputs/ 下，.csv 或 .xlsx）", true),
                   ("group_by", "string", "分组列名，例如 部门", true),
                   ("sum_column", "string", "求和金额列名，例如 销售额", true),
                   ("where_column", "string", "筛选列名（可选）", false),
                   ("where_values", "array", "筛选列的匹配值数组（与 where_column 搭配）", false),
                   ("where_mode", "string", "include=仅保留匹配值（默认），exclude=排除匹配值", false),
                   ("group_mapping", "object", "分组值映射（可选），如 {\"销售一部\":\"华东\",\"销售二部\":\"华北\"}，未列出的值保持原样", false)));

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "paths", "group_by", "sum_column",
                "where_column", "where_values", "where_mode", "group_mapping");
            var paths = ToolArgs.RequireStringArray(args, "paths", MaxPaths);
            var groupBy = ToolArgs.RequireString(args, "group_by");
            var sumColumn = ToolArgs.RequireString(args, "sum_column");
            var whereColumn = ToolArgs.OptionalString(args, "where_column", 100);
            var whereValues = ToolArgs.OptionalStringArray(args, "where_values", 1000);
            var whereMode = ToolArgs.OptionalString(args, "where_mode", 10) ?? "include";
            var groupMapping = ToolArgs.OptionalObject(args, "group_mapping", MaxMappingEntries);

            if (whereMode != "include" && whereMode != "exclude")
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "where_mode 只允许 include 或 exclude。");
            }
            if (whereColumn != null && whereValues == null)
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "使用 where_column 时必须同时提供 where_values。");
            }
            if (whereColumn == null && whereValues != null)
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "使用 where_values 时必须同时提供 where_column。");
            }
            if (groupMapping != null)
            {
                foreach (var prop in groupMapping.Properties())
                {
                    if (prop.Value.Type != JTokenType.String)
                    {
                        throw new ToolException(ErrorCodes.InvalidArguments,
                            "group_mapping 的值必须是字符串：键 " + prop.Name);
                    }
                }
            }

            long totalBytes = 0;
            var keptRows = 0;
            var filteredOut = 0;
            var sums = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var path in paths)
            {
                if (!seen.Add(path))
                {
                    throw new ToolException(ErrorCodes.InvalidArguments, "重复的文件路径：" + path);
                }
                if (!TableReading.IsSupported(path))
                {
                    throw new ToolException(ErrorCodes.UnsupportedFormat,
                        "不支持的表格格式（仅 .csv/.xlsx）：" + path);
                }
                cancellationToken.ThrowIfCancellationRequested();

                var full = _policy.ResolveReadablePath(path);
                var table = TableReading.Read(full, path);
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
                var whereIndex = -1;
                if (whereColumn != null)
                {
                    whereIndex = Array.IndexOf(table.Header, whereColumn);
                    if (whereIndex < 0)
                    {
                        throw new ToolException(ErrorCodes.CsvInvalid,
                            $"文件 {path} 缺少筛选列：{whereColumn}；实际列：{string.Join(",", table.Header)}。");
                    }
                }
                if (HasDuplicate(table.Header))
                {
                    throw new ToolException(ErrorCodes.CsvInvalid, "表头存在重复列名：" + path);
                }

                for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
                {
                    var row = table.Rows[rowIndex];

                    // 筛选（先于计数与求和）。
                    if (whereIndex >= 0)
                    {
                        var cell = row[whereIndex].Trim();
                        var match = whereValues!.Contains(cell, StringComparer.Ordinal);
                        if (whereMode == "exclude") match = !match;
                        if (!match)
                        {
                            filteredOut++;
                            continue;
                        }
                    }

                    keptRows++;
                    if (keptRows + filteredOut > CsvReading.MaxDataRows)
                    {
                        throw new ToolException(ErrorCodes.LimitExceeded,
                            "本任务数据行总数超过 100000 限制。");
                    }

                    var group = row[groupIndex].Trim();
                    if (group.Length == 0)
                    {
                        throw new ToolException(ErrorCodes.CsvInvalid,
                            $"{path} 第 {rowIndex + 1} 条记录的 {groupBy} 为空。");
                    }
                    if (groupMapping != null && groupMapping.TryGetValue(group, out var mapped))
                    {
                        group = (string?)mapped ?? group;
                        if (group.Length == 0)
                        {
                            throw new ToolException(ErrorCodes.CsvInvalid,
                                $"{path} 第 {rowIndex + 1} 条记录映射后的分组名为空。");
                        }
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
                        sums[group] = sums.TryGetValue(group, out var acc)
                            ? checked(acc + amount.Value) : amount.Value;
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

            var filterDescription = whereColumn == null ? ""
                : $"筛选：{whereColumn} {(whereMode == "exclude" ? "不等于" : "等于")} " +
                  string.Join("、", whereValues!) + $"（排除 {filteredOut} 条记录）";
            var mappingDescription = groupMapping == null || groupMapping.Count == 0 ? ""
                : "分组映射：" + string.Join("、",
                      groupMapping.Properties().Select(p => p.Name + "→" + (string?)p.Value));

            var result = new AggregationResult("", groupBy, sumColumn, paths,
                keptRows, sorted, grandTotal, filterDescription, mappingDescription, filteredOut);
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
                ["record_count"] = keptRows,
                ["group_count"] = sorted.Count,
                ["filtered_out_count"] = filteredOut,
                ["filter"] = filterDescription,
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
