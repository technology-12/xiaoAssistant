using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using WorkAssistant.Core;
using WorkAssistant.Tools;
using System.Threading.Tasks; // XP 线：GetAwaiter 扩展需要该导入

namespace WorkAssistant.Tests.Console
{
    /// <summary>CSV 工具与聚合规则（设计 §9.1 / 验收 14.1-1/2/3）。</summary>
    public static class CsvToolTests
    {
        public static void Register()
        {
            Test.Add("CSV：标准两文件样例输出 150.40 / 220.60 / 371.00", StandardDemo);
            Test.Add("CSV：引号内逗号与换行、转义引号", QuotedFields);
            Test.Add("CSV：GB18030 编码检测", Gb18030);
            Test.Add("CSV：负数与多位小数边界", NegativeAmounts);
            Test.Add("CSV：空文件与仅表头", EmptyFiles);
            Test.Add("CSV：坏金额报错并含路径行号", BadAmount);
            Test.Add("CSV：重复表头与缺列", HeaderErrors);
            Test.Add("CSV：溢出立即失败", Overflow);
            Test.Add("CSV：字段超 64KiB 拒绝", OversizedField);
            Test.Add("CSV：源文件在任务后保持不变", SourcesUntouched);
            Test.Add("CSV：输出 UTF-8 BOM、CRLF、公式注入防护", OutputEncoding);
        }

        private static string NewWs(params (string name, string text)[] files)
        {
            var root = Path.Combine(Path.GetTempPath(), "wa-csv-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(root, "inputs"));
            foreach (var (name, text) in files)
            {
                File.WriteAllBytes(Path.Combine(root, "inputs", name), Encoding.UTF8.GetBytes(text));
            }
            return root;
        }

        private static (AggregateCsvTool tool, ToolContext ctx) Setup(string root)
        {
            var policy = new WorkspacePolicy(root);
            var store = new InMemoryAggregationStore();
            var ctx = new ToolContext("task_test", policy, store, CancellationToken.None);
            return (new AggregateCsvTool(policy), ctx);
        }

        private static void StandardDemo()
        {
            var ws = DemoRunner.CopySamplesToTemp();
            var (record, store, _) = DemoRunner.Run(ws);

            var eventsText = string.Join(" | ",
                store.LoadEvents(record.TaskId).Select(e => e.Kind + " " + e.Summary));
            Test.Equal(TaskState.Completed, record.State,
                "任务应完成（实际 " + record.State + "：" + record.ErrorCode + " " + record.ErrorMessage +
                "；事件：" + eventsText + "）");
            Test.Equal(2, record.Artifacts.Count, "应有两个成果");

            var summary = DemoRunner.ReadArtifactText(ws, record.Artifacts.Single(a => a.Path.EndsWith("summary.csv")).Path);
            Test.Check(summary.Contains("销售一部,150.40"), "销售一部 150.40：" + summary);
            Test.Check(summary.Contains("销售二部,220.60"), "销售二部 220.60：" + summary);

            var report = DemoRunner.ReadArtifactText(ws, record.Artifacts.Single(a => a.Path.EndsWith("report.md")).Path);
            Test.Check(report.Contains("371.00"), "报告应含总额 371.00");
            Test.Check(report.Contains("4"), "报告应含记录数 4");
            Test.Check(report.Contains("2 个"), "报告应含来源文件数 2");
        }

        private static void QuotedFields()
        {
            var ws = NewWs(("quoted.csv",
                "部门,销售额\r\n\"华东, 一区\",10.00\r\n\"多行\r\n部门\",5.50\r\n\"含\"\"引号\"\",的部门\",1.00\r\n"));
            var (tool, ctx) = Setup(ws);
            var result = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/quoted.csv\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(result.Ok, "应成功：" + (result.Error?.Message ?? ""));
            Test.Equal(3, (int)result.Data["record_count"]!, "应有 3 条记录");
            Test.Equal("16.50", (string)result.Data["total"]!, "合计 16.50");
        }

        private static void Gb18030()
        {
            var root = Path.Combine(Path.GetTempPath(), "wa-csv-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(Path.Combine(root, "inputs"));
            var gb = Encoding.GetEncoding("GB18030");
            File.WriteAllBytes(Path.Combine(root, "inputs", "中文部门数据.csv"),
                gb.GetBytes("部门,销售额\r\n北京一部,12.34\r\n上海二部,56.78\r\n"));
            // GB18030 字节流不是合法 UTF-8（中文部分），应回退 GB18030 读取。
            var policy = new WorkspacePolicy(root);
            var preview = new PreviewCsvTool(policy);
            var ctx = new ToolContext("t", policy, new InMemoryAggregationStore(), CancellationToken.None);
            var result = preview.ExecuteAsync(ctx,
                "{\"path\":\"inputs/中文部门数据.csv\",\"max_rows\":5}", CancellationToken.None)
                .GetAwaiter().GetResult();
            Test.Check(result.Ok, "预览应成功：" + (result.Error?.Message ?? ""));
            Test.Equal("gb18030", (string)result.Data["encoding"]!, "编码应为 gb18030");
            Test.Equal(2, (int)result.Data["total_data_rows"]!, "2 行数据");
        }

        private static void NegativeAmounts()
        {
            var ws = NewWs(("neg.csv", "部门,销售额\r\n退换组,-10.05\r\n正常组,20.10\r\n退换组,-0.95\r\n"));
            var (tool, ctx) = Setup(ws);
            var result = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/neg.csv\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(result.Ok, "负数应被接受：" + (result.Error?.Message ?? ""));
            Test.Equal("9.10", (string)result.Data["total"]!, "合计 9.10（-10.05+20.10-0.95）");
            Test.Equal(2, (int)result.Data["group_count"]!, "两个分组");
        }

        private static void EmptyFiles()
        {
            var ws = NewWs(("empty.csv", ""), ("header.csv", "部门,销售额\r\n"));
            var (tool, ctx) = Setup(ws);

            var r1 = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/empty.csv\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!r1.Ok, "空文件应失败");
            Test.Equal(ErrorCodes.CsvInvalid, r1.Error!.Code, "空文件错误码");

            var r2 = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/header.csv\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(r2.Ok, "仅表头应成功（0 条记录）：" + (r2.Error?.Message ?? ""));
            Test.Equal(0, (int)r2.Data["record_count"]!, "0 条记录");
            Test.Equal("0.00", (string)r2.Data["total"]!, "合计 0.00");
        }

        private static void BadAmount()
        {
            var ws = NewWs(("bad.csv", "部门,销售额\r\n一组,10.00\r\n二组,¥20.00\r\n三组,1.234\r\n"));
            var (tool, ctx) = Setup(ws);
            var result = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/bad.csv\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!result.Ok, "坏金额应失败");
            Test.Equal(ErrorCodes.CsvInvalid, result.Error!.Code, "错误码");
            Test.Check(result.Error.Message.Contains("第 2 条"), "错误应含记录序号：" + result.Error.Message);
            Test.Check(result.Error.Message.Contains("inputs/bad.csv"), "错误应含相对路径");
        }

        private static void HeaderErrors()
        {
            var ws = NewWs(
                ("dup.csv", "部门,部门,销售额\r\n一组,1,2.00\r\n"),
                ("missing.csv", "部门,金额\r\n一组,2.00\r\n"));
            var (tool, ctx) = Setup(ws);

            var r1 = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/dup.csv\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!r1.Ok && r1.Error!.Code == ErrorCodes.CsvInvalid, "重复表头应失败");

            var r2 = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/missing.csv\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!r2.Ok && r2.Error!.Code == ErrorCodes.CsvInvalid, "缺列应失败");
            Test.Check(r2.Error!.Message.Contains("销售额"), "错误应说明缺失列");
        }

        private static void Overflow()
        {
            // 两个等于 decimal 上限的合法值，求和溢出。
            var nearMax = "79228162514264337593543950335";
            var content = "部门,销售额\r\n甲," + nearMax + "\r\n乙," + nearMax + "\r\n";
            var ws = NewWs(("big.csv", content));
            var (tool, ctx) = Setup(ws);
            var result = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/big.csv\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!result.Ok, "溢出应失败：" + result.Error?.Message);
            Test.Equal(ErrorCodes.LimitExceeded, result.Error!.Code, "溢出错误码");

            // 单值超出 decimal 范围：属于无效金额而非溢出。
            var ws2 = NewWs(("huge.csv", "部门,销售额\r\n甲," + new string('9', 30) + "\r\n"));
            var (tool2, ctx2) = Setup(ws2);
            var r2 = tool2.ExecuteAsync(ctx2,
                "{\"paths\":[\"inputs/huge.csv\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!r2.Ok, "超大单值应失败");
            Test.Equal(ErrorCodes.CsvInvalid, r2.Error!.Code, "超大单值错误码");
        }

        private static void OversizedField()
        {
            var huge = new string('部', 70 * 1024);
            var ws = NewWs(("huge.csv", "部门,销售额\r\n" + huge + ",1.00\r\n"));
            var (tool, ctx) = Setup(ws);
            var result = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/huge.csv\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!result.Ok, "超 64KiB 字段应失败");
            Test.Equal(ErrorCodes.LimitExceeded, result.Error!.Code, "超限错误码");
        }

        private static void SourcesUntouched()
        {
            var ws = DemoRunner.CopySamplesToTemp();
            var before = Directory.GetFiles(Path.Combine(ws, "inputs"))
                .OrderBy(p => p)
                .Select(File.ReadAllBytes)
                .ToList();
            DemoRunner.Run(ws);
            var after = Directory.GetFiles(Path.Combine(ws, "inputs"))
                .OrderBy(p => p)
                .Select(File.ReadAllBytes)
                .ToList();
            Test.Equal(before.Count, after.Count, "文件数不变");
            for (var i = 0; i < before.Count; i++)
            {
                Test.Equal(before[i].Length, after[i].Length, "文件 " + i + " 大小不变");
                Test.Check(System.Linq.Enumerable.SequenceEqual(before[i], after[i]), "文件 " + i + " 内容不变");
            }
        }

        private static void OutputEncoding()
        {
            var ws = NewWs(("inject.csv", "部门,销售额\r\n=HYPERLINK(\"x\"),1.00\r\n+Sum(1),2.00\r\n普通部门,3.00\r\n"));
            var policy = new WorkspacePolicy(ws);
            var store = new InMemoryAggregationStore();
            var ctx = new ToolContext("task_enc", policy, store, CancellationToken.None);
            var agg = new AggregateCsvTool(policy);
            var aggResult = agg.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/inject.csv\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(aggResult.Ok, "聚合成功：" + (aggResult.Error?.Message ?? ""));

            var export = new ExportSummaryTool(policy);
            var exportResult = export.ExecuteAsync(ctx,
                "{\"aggregation_id\":\"" + (string)aggResult.Data["aggregation_id"]! + "\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(exportResult.Ok, "导出成功：" + (exportResult.Error?.Message ?? ""));

            var csvPath = Path.Combine(ws, exportResult.Artifacts.Single(a => a.Path.EndsWith("summary.csv")).Path.Replace('/', Path.DirectorySeparatorChar));
            var raw = File.ReadAllBytes(csvPath);
            Test.Check(raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, "UTF-8 BOM");
            var text = Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
            Test.Check(text.Contains("\r\n"), "CRLF 换行");
            Test.Check(text.Contains("'=HYPERLINK"), "公式注入防护（=）");
            Test.Check(text.Contains("'+Sum(1)"), "公式注入防护（+）");
            Test.Check(text.Contains("普通部门,3.00"), "普通部门不受影响");
        }
    }
}
