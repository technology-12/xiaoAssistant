using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using WorkAssistant.Core;
using WorkAssistant.Tools;
using System.Threading.Tasks; // XP 线：GetAwaiter 扩展需要该导入

namespace WorkAssistant.Tests.Console
{
    /// <summary>
    /// 办公能力测试（正式版 §8.2/§8.3）：XLSX 读写、DOCX 生成与模板填充、PDF 提取。
    /// 样例全部由 SampleWriter 生成（dogfood），不依赖外部文件。
    /// </summary>
    public static class OfficeFileTests
    {
        public static void Register()
        {
            Test.Add("XLSX：写出→读回 roundtrip（表头/数值/行序）", XlsxRoundTrip);
            Test.Add("XLSX：aggregate_table 汇总总额 371.00", XlsxAggregate);
            Test.Add("XLSX：确定性输出（两次生成字节一致）", XlsxDeterministic);
            Test.Add("XLSX：坏文件 → UNSUPPORTED_FORMAT", XlsxInvalid);
            Test.Add("Excel 演示：export_xlsx 交付 .xlsx 成果", XlsxEndToEnd);
            Test.Add("DOCX：generate_docx 生成含表格的报告", DocxGenerate);
            Test.Add("DOCX：模板填充成功与未提供字段报错", DocxTemplate);
            Test.Add("DOCX：preview_docx_fields 列出占位符", DocxFields);
            Test.Add("PDF：两页提取（英文 literal + 中文 hex/CMap）", PdfTwoPages);
            Test.Add("PDF：无文字（图像型）→ UNSUPPORTED_FORMAT 且提示扫描件", PdfScanned);
            Test.Add("PDF：非 PDF 文件 → UNSUPPORTED_FORMAT", PdfNotPdf);
            Test.Add("aggregate_table：where 筛选与 group_mapping 映射", AggregateFilterMapping);
            Test.Add("list_files：扩展名过滤与非法扩展名拒绝", ListFilesExtensions);
        }

        private static string TempDir() =>
            Path.Combine(Path.GetTempPath(), "wa-office-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        private static string PrepareWorkspace()
        {
            var root = TempDir();
            Directory.CreateDirectory(Path.Combine(root, "inputs"));
            // 标准演示 CSV + 生成 xlsx/pdf 样例。
            foreach (var file in Directory.GetFiles(Path.Combine(
                         DemoRunner.FindSamplesWorkspace(), "inputs"), "*.csv"))
            {
                File.Copy(file, Path.Combine(root, "inputs", Path.GetFileName(file)));
            }
            SampleWriter.WriteAll(Path.Combine(root, "inputs"));
            WorkspacePolicy.EnsureLayout(root);
            return root;
        }

        // ---- XLSX ------------------------------------------------------------

        private static void XlsxRoundTrip()
        {
            var root = PrepareWorkspace();
            var path = Path.Combine(root, "inputs", "sales.xlsx");
            Test.Check(File.Exists(path), "样例 xlsx 已生成");
            var table = XlsxReading.ReadFirstSheet(path, "inputs/sales.xlsx");
            Test.Equal(2, table.Header.Length, "两列");
            Test.Equal("部门", table.Header[0], "表头列 1");
            Test.Equal("销售额", table.Header[1], "表头列 2");
            Test.Equal(4, table.Rows.Count, "4 行数据");
            Test.Equal("销售一部", table.Rows[0][0], "行 1 分组");
            Test.Equal("100.1", table.Rows[0][1], "行 1 金额（invariant 文本）");
        }

        private static void XlsxAggregate()
        {
            var root = PrepareWorkspace();
            var policy = new WorkspacePolicy(root);
            var tool = new AggregateTableTool(policy);
            var ctx = new ToolContext("t-agg", policy, new InMemoryAggregationStore(),
                CancellationToken.None);
            var result = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/sales.xlsx\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(result.Ok, "聚合成功：" + (result.Error?.Message ?? ""));
            Test.Equal("371.00", (string)result.Data["total"]!, "总额 371.00");
            Test.Equal(4, (int)result.Data["record_count"]!, "4 条记录");
            Test.Equal(2, (int)result.Data["group_count"]!, "2 个分组");
        }

        private static void XlsxDeterministic()
        {
            var header = new List<string> { "A", "B" };
            var rows = new List<IList<XlsxCell>>
            {
                new[] { XlsxCell.Text2("x"), XlsxCell.Number(1.5m) }
            };
            var a = XlsxWriting.Build("S", header, rows);
            var b = XlsxWriting.Build("S", header, rows);
            Test.Equal(a.Length, b.Length, "两次生成长度一致");
            Test.Check(a.SequenceEqual(b), "字节完全一致（可复现 SHA-256）");
        }

        private static void XlsxInvalid()
        {
            var root = PrepareWorkspace();
            var policy = new WorkspacePolicy(root);
            var bad = Path.Combine(root, "inputs", "bad.xlsx");
            File.WriteAllBytes(bad, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x01 });
            Test.ExpectToolError(ErrorCodes.UnsupportedFormat,
                () => XlsxReading.ReadFirstSheet(bad, "inputs/bad.xlsx"), "坏 zip 报格式不支持");
            File.WriteAllBytes(bad, new byte[] { 1, 2, 3 });
            Test.ExpectToolError(ErrorCodes.UnsupportedFormat,
                () => XlsxReading.ReadFirstSheet(bad, "inputs/bad.xlsx"), "非 zip 报格式不支持");
        }

        private static void XlsxEndToEnd()
        {
            var root = PrepareWorkspace();
            var (record, _, _) = DemoRunner.Run(root, FakeBehavior.XlsxDemo,
                "汇总 inputs 中的销售 Excel（xlsx），按部门统计销售额，导出 XLSX 汇总表。");
            Test.Equal(TaskState.Completed, record.State, "任务完成：" + record.ErrorMessage);
            Test.Equal(1, record.Artifacts.Count, "1 个成果");
            Test.Check(record.Artifacts[0].Path.EndsWith("summary.xlsx"), "成果为 xlsx：" + record.Artifacts[0].Path);
            var full = Path.Combine(root, record.Artifacts[0].Path.Replace('/', Path.DirectorySeparatorChar));
            Test.Check(File.Exists(full), "成果文件存在");
            // 导出的 xlsx 可再次被自己的读取器解析（导出后重新解析核验）。
            var back = XlsxReading.ReadFirstSheet(full, record.Artifacts[0].Path);
            Test.Equal(2, back.Rows.Count, "导出表含 2 个分组行");
            Test.Equal("371.00", back.Rows.Select(r => r[1]).Sum(v => decimal.Parse(v,
                System.Globalization.CultureInfo.InvariantCulture)).ToString("0.00",
                System.Globalization.CultureInfo.InvariantCulture), "导出表合计 371.00");
        }

        // ---- DOCX ------------------------------------------------------------

        private static void DocxGenerate()
        {
            var root = PrepareWorkspace();
            var (record, _, _) = DemoRunner.Run(root, FakeBehavior.DocxReport,
                "把 inputs 中的销售 CSV 按部门汇总，生成 Word 汇总报告。");
            Test.Equal(TaskState.Completed, record.State, "任务完成：" + record.ErrorMessage);
            Test.Equal(1, record.Artifacts.Count, "1 个成果");
            Test.Check(record.Artifacts[0].Type == "report_docx", "成果类型 report_docx");
            var full = Path.Combine(root, record.Artifacts[0].Path.Replace('/', Path.DirectorySeparatorChar));
            PackageZip.AssertZipOpenable(full, record.Artifacts[0].Path);
            var documentXml = PackageZip.ReadUtf8(PackageZip.ReadEntry(full, "word/document.xml")!);
            Test.Check(documentXml.Contains("销售数据汇总报告"), "包含标题");
            Test.Check(documentXml.Contains("371.00"), "包含合计金额");
            Test.Check(documentXml.Contains("<w:tbl>"), "包含表格");
            Test.Check(documentXml.Contains("decimal"), "包含校验说明");
        }

        private static void DocxTemplate()
        {
            var root = PrepareWorkspace();
            var policy = new WorkspacePolicy(root);
            // 用自身生成器构造模板：正文带 {{客户}}/{{月份}} 占位符。
            var templateBlocks = new List<DocxBlock>
            {
                new DocxParagraph("客户：{{客户}}"),
                new DocxParagraph("月份：{{月份}}"),
                new DocxParagraph("本报告由工作助手生成。")
            };
            File.WriteAllBytes(Path.Combine(root, "inputs", "模板.docx"),
                DocxWriting.BuildReport(new DocxDocument("", templateBlocks)));

            var tool = new FillDocxTemplateTool(policy);
            var ctx = new ToolContext("t-docx", policy, new InMemoryAggregationStore(),
                CancellationToken.None);

            var ok = tool.ExecuteAsync(ctx,
                "{\"template_path\":\"inputs/模板.docx\",\"fields\":{\"客户\":\"示例公司\",\"月份\":\"2026年10月\"}}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(ok.Ok, "填充成功：" + (ok.Error?.Message ?? ""));
            var output = Path.Combine(root, ok.Artifacts.Single().Path.Replace('/', Path.DirectorySeparatorChar));
            Test.Check(File.Exists(output), "输出存在");
            var text = PackageZip.ReadUtf8(PackageZip.ReadEntry(output, "word/document.xml")!);
            Test.Check(text.Contains("示例公司"), "客户字段已替换");
            Test.Check(text.Contains("2026年10月"), "月份字段已替换");
            Test.Check(!text.Contains("{{"), "无残留占位符");
            Test.Check(!File.ReadAllText(Path.Combine(root, "inputs", "模板.docx")).Contains("示例公司"),
                "模板原件未被修改");

            var missing = tool.ExecuteAsync(ctx,
                "{\"template_path\":\"inputs/模板.docx\",\"fields\":{\"客户\":\"x\"}}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!missing.Ok, "缺少字段应失败");
            Test.Equal(ErrorCodes.InvalidArguments, missing.Error!.Code, "错误码");
            Test.Check(missing.Error.Message.Contains("月份"), "错误列出缺失字段");
        }

        private static void DocxFields()
        {
            var root = PrepareWorkspace();
            var policy = new WorkspacePolicy(root);
            var templateBlocks = new List<DocxBlock> { new DocxParagraph("{{项目名}} 结项于 {{日期}}") };
            File.WriteAllBytes(Path.Combine(root, "inputs", "t2.docx"),
                DocxWriting.BuildReport(new DocxDocument("", templateBlocks)));

            var tool = new PreviewDocxFieldsTool(policy);
            var ctx = new ToolContext("t-docx2", policy, new InMemoryAggregationStore(),
                CancellationToken.None);
            var result = tool.ExecuteAsync(ctx,
                "{\"template_path\":\"inputs/t2.docx\"}", CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(result.Ok, "读取成功");
            var fields = (Newtonsoft.Json.Linq.JArray)result.Data["fields"]!;
            Test.Equal(2, fields.Count, "两个占位符");
            var names = fields.Select(f => (string)f!).ToList();
            Test.Check(names.Contains("项目名") && names.Contains("日期"), "字段名正确");
        }

        // ---- PDF -------------------------------------------------------------

        private static void PdfTwoPages()
        {
            var root = PrepareWorkspace();
            var path = Path.Combine(root, "inputs", "产品说明.pdf");
            var result = PdfText.Extract(path, "inputs/产品说明.pdf");
            Test.Equal(2, result.PageCount, "两页");
            Test.Equal(2, result.Pages.Count, "两页文本");
            Test.Check(result.Pages[0].Text.Contains("Win7 Work Assistant"), "第 1 页英文 literal：" + result.Pages[0].Text);
            Test.Check(result.Pages[0].Text.Contains("Product manual"), "第 1 页第二行");
            Test.Check(result.Pages[1].Text.Contains("你好世界"), "第 2 页中文 bfchar/bfrange：" + result.Pages[1].Text);
            Test.Check(result.Pages[1].Text.Contains("助手册"), "第 2 页 bfrange 尾部");
            Test.Check(!result.LikelyScanned, "文本 PDF 不判为扫描件");
            Test.Check(result.TotalChars > 10, "总字符数：" + result.TotalChars);
        }

        private static void PdfScanned()
        {
            var root = PrepareWorkspace();
            var path = Path.Combine(root, "inputs", "扫描件.pdf");
            File.WriteAllBytes(path, SampleWriter.BuildScannedLikePdf());

            var policy = new WorkspacePolicy(root);
            var tool = new ExtractPdfTextTool(policy);
            var ctx = new ToolContext("t-pdf", policy, new InMemoryAggregationStore(),
                CancellationToken.None);
            var result = tool.ExecuteAsync(ctx,
                "{\"path\":\"inputs/扫描件.pdf\"}", CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!result.Ok, "无文字应失败");
            Test.Equal(ErrorCodes.UnsupportedFormat, result.Error!.Code, "错误码");
            Test.Check(result.Error.Message.Contains("扫描"), "提示可能为扫描件：" + result.Error.Message);
        }

        private static void PdfNotPdf()
        {
            var root = PrepareWorkspace();
            var path = Path.Combine(root, "inputs", "fake.pdf");
            File.WriteAllText(path, "这不是 PDF。");
            Test.ExpectToolError(ErrorCodes.UnsupportedFormat,
                () => PdfText.Extract(path, "inputs/fake.pdf"), "非 PDF 报格式不支持");
        }

        // ---- aggregate_table 扩展 ---------------------------------------------

        private static void AggregateFilterMapping()
        {
            var root = PrepareWorkspace();
            var policy = new WorkspacePolicy(root);
            var tool = new AggregateTableTool(policy);
            var ctx = new ToolContext("t-afm", policy, new InMemoryAggregationStore(),
                CancellationToken.None);

            var filtered = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/sales-a.csv\",\"inputs/sales-b.csv\"]," +
                "\"group_by\":\"部门\",\"sum_column\":\"销售额\"," +
                "\"where_column\":\"部门\",\"where_values\":[\"销售一部\"]}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(filtered.Ok, "筛选聚合成功");
            Test.Equal("150.40", (string)filtered.Data["total"]!, "只留销售一部 → 150.40");
            Test.Equal(2, (int)filtered.Data["record_count"]!, "保留 2 条");
            Test.Equal(2, (int)filtered.Data["filtered_out_count"]!, "排除 2 条");

            var mapped = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/sales-a.csv\",\"inputs/sales-b.csv\"]," +
                "\"group_by\":\"部门\",\"sum_column\":\"销售额\"," +
                "\"group_mapping\":{\"销售一部\":\"华东\",\"销售二部\":\"华北\"}}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(mapped.Ok, "映射聚合成功");
            Test.Equal(2, (int)mapped.Data["group_count"]!, "映射后 2 组");
            var preview = (Newtonsoft.Json.Linq.JArray)mapped.Data["preview"]!;
            Test.Check(preview[0]!["部门"]!.ToString() == "华东", "映射组名生效");

            var badMode = tool.ExecuteAsync(ctx,
                "{\"paths\":[\"inputs/sales-a.csv\"],\"group_by\":\"部门\",\"sum_column\":\"销售额\"," +
                "\"where_mode\":\"delete\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!badMode.Ok, "非法 where_mode 拒绝");
        }

        private static void ListFilesExtensions()
        {
            var root = PrepareWorkspace();
            var policy = new WorkspacePolicy(root);
            var tool = new ListFilesTool(policy);
            var ctx = new ToolContext("t-lf", policy, new InMemoryAggregationStore(),
                CancellationToken.None);

            var pdfs = tool.ExecuteAsync(ctx,
                "{\"directory\":\"inputs\",\"extensions\":[\".pdf\"]}", CancellationToken.None)
                .GetAwaiter().GetResult();
            Test.Check(pdfs.Ok, "按扩展名列出成功");
            var files = ((System.Collections.Generic.List<string>)pdfs.Data["files"]!).ToList();
            Test.Equal(1, files.Count, "只有 1 个 pdf");
            Test.Check(files[0].EndsWith(".pdf"), "是 pdf 文件");

            var bad = tool.ExecuteAsync(ctx,
                "{\"directory\":\"inputs\",\"extensions\":[\".exe\"]}", CancellationToken.None)
                .GetAwaiter().GetResult();
            Test.Check(!bad.Ok, "非法扩展名拒绝");
            Test.Equal(ErrorCodes.InvalidArguments, bad.Error!.Code, "错误码");
        }
    }
}
