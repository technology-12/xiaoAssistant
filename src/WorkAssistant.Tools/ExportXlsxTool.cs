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
    /// export_xlsx：把聚合结果写成 outputs/&lt;task_id&gt;/summary.xlsx。
    /// 只接受 aggregation_id；金额来自已验证的聚合结果（原生数值单元格，Excel 可直接计算）。
    /// </summary>
    public sealed class ExportXlsxTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;

        public ExportXlsxTool(WorkspacePolicy policy) => _policy = policy;

        protected override string FallbackErrorCode => ErrorCodes.OutputWriteFailed;

        public override ToolDefinition Definition { get; } = new ToolDefinition(
            "export_xlsx",
            "将聚合结果导出为 XLSX 汇总表（Excel 数值单元格）到任务输出目录，返回成果清单。" +
            "只能引用本任务 aggregate_csv/aggregate_table 返回的 aggregation_id。",
            Schema(("aggregation_id", "string", "聚合工具返回的聚合 ID", true),
                   ("file_name", "string", "输出文件名（可选，默认 summary.xlsx）", false)),
            producesArtifacts: true);

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "aggregation_id", "file_name");
            var aggregationId = ToolArgs.RequireString(args, "aggregation_id");
            var fileName = ToolArgs.OptionalString(args, "file_name", 120) ?? "summary.xlsx";
            if (!fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "输出文件名必须以 .xlsx 结尾。");
            }

            var aggregation = context.Aggregations.Find(aggregationId);
            if (aggregation == null)
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "aggregation_id 无效或已过期（不能跨任务引用）。");
            }

            var header = new List<string> { aggregation.GroupByColumn, aggregation.SumColumn };
            var rows = aggregation.Groups
                .Select(kv => new[] { XlsxCell.Text2(kv.Key), XlsxCell.Number(kv.Value) })
                .ToList<IList<XlsxCell>>();
            var bytes = XlsxWriting.Build("汇总表", header, rows);
            var artifacts = TaskOutputs.CommitFiles(_policy, context.TaskId,
                new[] { (fileName, bytes, "summary_xlsx") }, (path, _) => ExportValidators.AggregateTable(path, aggregation, true));

            var list = artifacts.Select(a => a.Path).ToList();
            return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?>
            {
                ["aggregation_id"] = aggregationId,
                ["record_count"] = aggregation.RecordCount,
                ["group_count"] = aggregation.Groups.Count,
                ["total"] = AmountParser.Format(aggregation.Total),
                ["artifacts"] = list
            }, artifacts));
        }
    }

    /// <summary>
    /// generate_docx：用受控文档模型把聚合结果生成为 Word 报告（标题/段落/表格）。
    /// 模型只能提供标题与说明文字（纯文本），不能注入 XML；数字全部来自聚合结果。
    /// </summary>
    public sealed class GenerateDocxTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;

        public GenerateDocxTool(WorkspacePolicy policy) => _policy = policy;

        protected override string FallbackErrorCode => ErrorCodes.OutputWriteFailed;

        public override ToolDefinition Definition { get; } = new ToolDefinition(
            "generate_docx",
            "将聚合结果生成 Word（.docx）汇总报告：标题、说明段落、数据表与校验说明。" +
            "只能引用本任务聚合工具返回的 aggregation_id；标题与说明为纯文本。",
            Schema(("aggregation_id", "string", "聚合工具返回的聚合 ID", true),
                   ("title", "string", "报告标题（可选，默认 销售数据汇总报告）", false),
                   ("summary_text", "string", "报告开头的说明文字（可选，纯文本，500 字以内）", false),
                   ("file_name", "string", "输出文件名（可选，默认 report.docx）", false)),
            producesArtifacts: true);

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "aggregation_id", "title", "summary_text", "file_name");
            var aggregationId = ToolArgs.RequireString(args, "aggregation_id");
            var title = ToolArgs.OptionalString(args, "title", 120) ?? "销售数据汇总报告";
            var summaryText = ToolArgs.OptionalString(args, "summary_text", 500) ?? "";
            var fileName = ToolArgs.OptionalString(args, "file_name", 120) ?? "report.docx";
            if (!fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "输出文件名必须以 .docx 结尾。");
            }

            var aggregation = context.Aggregations.Find(aggregationId);
            if (aggregation == null)
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "aggregation_id 无效或已过期（不能跨任务引用）。");
            }

            var blocks = new List<DocxBlock>();
            if (summaryText.Length > 0)
            {
                blocks.Add(new DocxParagraph(OutputSanitizer.PlainText(summaryText)));
            }
            blocks.Add(new DocxHeading("数据口径"));
            blocks.Add(new DocxBullet("来源文件（" + aggregation.SourcePaths.Count + " 个）："));
            foreach (var p in aggregation.SourcePaths)
            {
                blocks.Add(new DocxBullet("· " + p));
            }
            var caliber = new List<string>();
            if (aggregation.FilterDescription.Length > 0) caliber.Add(aggregation.FilterDescription);
            if (aggregation.MappingDescription.Length > 0) caliber.Add(aggregation.MappingDescription);
            blocks.Add(new DocxBullet(caliber.Count > 0
                ? string.Join("；", caliber)
                : "未应用筛选与分组映射（全量汇总）"));
            blocks.Add(new DocxBullet($"数据记录数：{aggregation.RecordCount}；分组数：{aggregation.Groups.Count}"));

            blocks.Add(new DocxHeading("分组汇总"));
            var tableRows = new List<DocxTableRow>
            {
                new(new[] { aggregation.GroupByColumn, aggregation.SumColumn })
            };
            foreach (var kv in aggregation.Groups)
            {
                tableRows.Add(new DocxTableRow(new[]
                {
                    OutputSanitizer.PlainText(kv.Key), AmountParser.Format(kv.Value)
                }));
            }
            tableRows.Add(new DocxTableRow(new[] { "合计", AmountParser.Format(aggregation.Total) }));
            blocks.Add(new DocxTable(tableRows));

            blocks.Add(new DocxHeading("校验说明"));
            blocks.Add(new DocxParagraph(
                "所有金额由程序使用精确十进制（decimal）按 invariant 格式计算，未使用模型心算结果；" +
                "输入文件保持只读，未修改源数据。"));

            var bytes = DocxWriting.BuildReport(new DocxDocument(title, blocks));
            var artifacts = TaskOutputs.CommitFiles(_policy, context.TaskId,
                new[] { (fileName, bytes, "report_docx") });

            return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?>
            {
                ["aggregation_id"] = aggregationId,
                ["total"] = AmountParser.Format(aggregation.Total),
                ["record_count"] = aggregation.RecordCount,
                ["artifacts"] = artifacts.Select(a => a.Path).ToList()
            }, artifacts));
        }
    }

    /// <summary>
    /// fill_docx_template：读取 inputs 中的 .docx 模板，替换 {{字段}} 后输出新文档。
    /// 输出是模板的副本，不修改模板原件；占位符必须完整位于单个文本块内。
    /// </summary>
    public sealed class FillDocxTemplateTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;

        public FillDocxTemplateTool(WorkspacePolicy policy) => _policy = policy;

        protected override string FallbackErrorCode => ErrorCodes.OutputWriteFailed;

        public override ToolDefinition Definition { get; } = new ToolDefinition(
            "fill_docx_template",
            "用输入目录中的 Word 模板（.docx，含 {{字段}} 占位符）生成文档：fields 指定字段值，" +
            "输出为模板副本（不修改模板）。可先用 preview_docx_fields 查看模板可用字段。",
            Schema(("template_path", "string", "模板文件的工作区相对路径（inputs/ 下）", true),
                   ("fields", "object", "字段值映射，键为占位符名（不含大括号），值为纯文本", true),
                   ("file_name", "string", "输出文件名（可选，默认以模板名生成）", false)),
            producesArtifacts: true);

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "template_path", "fields", "file_name");
            var templatePath = ToolArgs.RequireString(args, "template_path");
            var fieldsObj = ToolArgs.OptionalObject(args, "fields", 100);
            if (fieldsObj == null)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, "参数 fields 不能为空。");
            }
            var fileName = ToolArgs.OptionalString(args, "file_name", 120);

            var full = _policy.ResolveReadablePath(templatePath);
            if (!templatePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat,
                    "模板必须为 .docx 文件：" + templatePath);
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in fieldsObj.Properties())
            {
                if (prop.Value!.Type != JTokenType.String)
                {
                    throw new ToolException(ErrorCodes.InvalidArguments,
                        "fields 的值必须是字符串：键 " + prop.Name);
                }
                var value = OutputSanitizer.PlainText((string?)prop.Value ?? "");
                if (value.Length > 2000)
                {
                    throw new ToolException(ErrorCodes.LimitExceeded,
                        "字段值超过 2000 字符：" + prop.Name);
                }
                values[prop.Name] = value;
            }
            if (values.Count == 0)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, "fields 不能为空。");
            }

            fileName ??= System.IO.Path.GetFileNameWithoutExtension(templatePath) + "-filled.docx";
            if (!fileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "输出文件名必须以 .docx 结尾。");
            }

            var bytes = DocxWriting.FillTemplate(full, templatePath, values);
            var artifacts = TaskOutputs.CommitFiles(_policy, context.TaskId,
                new[] { (fileName, bytes, "filled_docx") });

            return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?>
            {
                ["template"] = templatePath,
                ["field_count"] = values.Count,
                ["artifacts"] = artifacts.Select(a => a.Path).ToList()
            }, artifacts));
        }
    }

    /// <summary>preview_docx_fields：列出模板中的 {{字段}} 名（只读，帮助模型正确填参）。</summary>
    public sealed class PreviewDocxFieldsTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;

        public PreviewDocxFieldsTool(WorkspacePolicy policy) => _policy = policy;

        protected override string FallbackErrorCode => ErrorCodes.UnsupportedFormat;

        public override ToolDefinition Definition { get; } = new ToolDefinition(
            "preview_docx_fields",
            "查看 Word 模板（.docx）中可填充的 {{字段}} 名列表。只读。",
            Schema(("template_path", "string", "模板文件的工作区相对路径（inputs/ 下）", true)));

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "template_path");
            var templatePath = ToolArgs.RequireString(args, "template_path");
            var full = _policy.ResolveReadablePath(templatePath);

            var fields = new JArray(DocxWriting.TemplateFields(full));
            return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?>
            {
                ["template"] = templatePath,
                ["fields"] = fields
            }));
        }
    }

    /// <summary>
    /// extract_pdf_text：提取文本型 PDF 的文字（按页），写为 Markdown 成果。
    /// 区分“没有可提取文本（可能扫描件）”与“空文档”；页码保留在成果中。
    /// </summary>
    public sealed class ExtractPdfTextTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;
        private readonly bool _allowFileOutput;

        public ExtractPdfTextTool(WorkspacePolicy policy, bool allowFileOutput = true)
        {
            _policy = policy;
            _allowFileOutput = allowFileOutput;
            Definition = CreateDefinition(allowFileOutput);
        }

        protected override string FallbackErrorCode => ErrorCodes.UnsupportedFormat;

        public override ToolDefinition Definition { get; }
        private static ToolDefinition CreateDefinition(bool allowFileOutput)
        {
            var schema = Schema(("path", "string", "PDF 文件的工作区相对路径（inputs/ 下）", true),
                   ("max_pages", "integer", "最多提取页数 1~200（默认 20）", false),
                   ("write_file", "boolean", "是否写出 Markdown 成果文件（默认 false；只读模式只能为 false）", false));
            if (!allowFileOutput) schema["properties"]!["write_file"]!["enum"] = new JArray(false);
            return new ToolDefinition(
            "extract_pdf_text",
            "提取 PDF 中的文字" + (OfficeCapabilities.LocalOcrSupported ? "（扫描页和较大图片混合页自动本地 OCR，识别文字需核对原页）" : "") + "。返回按页文本预览，" +
            (allowFileOutput ? "并可生成带页码引用的 Markdown 成果文件。" : "当前只读，禁止写出文件。"),
            schema, producesArtifacts: allowFileOutput);
        }

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "path", "max_pages", "write_file");
            var path = ToolArgs.RequireString(args, "path");
            var maxPages = ToolArgs.OptionalIntInRange(args, "max_pages", 20, 1, 200);
            if (args.TryGetValue("write_file", out var writeFlag) && writeFlag.Type != JTokenType.Boolean)
                throw new ToolException(ErrorCodes.InvalidArguments, "write_file 必须为布尔值。");
            var writeBool = args.TryGetValue("write_file", out var flag) && flag.Type == JTokenType.Boolean
                ? (bool)flag : false;
            if (writeBool && !_allowFileOutput)
                throw new ToolException(ErrorCodes.ToolPolicyDenied, "只读分析模式禁止导出 PDF 文字文件。");
            if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat,
                    "只支持 .pdf 文件：" + path);
            }

            var full = _policy.ResolveReadablePath(path);
            var result = PdfText.Extract(full, path, maxPages);

            if (result.TotalChars == 0)
            {
                var message = result.LikelyScanned
                    ? OfficeCapabilities.LocalOcrSupported
                        ? $"PDF 本地 OCR 未识别出文字（共 {result.PageCount} 页，检测到图像），请检查页面清晰度。"
                        : $"PDF 未识别出文字（共 {result.PageCount} 页，检测到图像）；本版本不支持扫描件 OCR。"
                    : OfficeCapabilities.LocalOcrSupported
                        ? $"PDF 提取与本地 OCR 均未识别出文字（共 {result.PageCount} 页），请检查扫描件清晰度或是否为空白页面。"
                        : $"PDF 未识别出文字（共 {result.PageCount} 页），请检查是否为空白页面；本版本不支持扫描件 OCR。";
                return XpTask.FromResult(ToolResult.Failure(ErrorCodes.UnsupportedFormat, message));
            }

            var data = new Dictionary<string, object?>
            {
                ["path"] = path,
                ["page_count"] = result.PageCount,
                ["total_chars"] = result.TotalChars,
                ["extracted_pages"] = result.Pages.Count, ["partial_document"] = result.Pages.Count < result.PageCount,
                ["scope_notice"] = "仅提取第 1~" + result.Pages.Count + " 页；预览文字不代表全文。" + result.OcrNotice,
                ["ocr_pages"] = result.OcrPages, ["low_confidence_pages"] = result.LowConfidencePages,
                ["likely_scanned"] = result.LikelyScanned
            };
            var preview = new JArray();
            foreach (var page in result.Pages.Take(3))
            {
                var sample = page.Text.Trim();
                if (sample.Length > 600) sample = sample.Substring(0, 600) + "…";
                preview.Add(new JObject
                {
                    ["page"] = page.PageNumber,
                    ["chars"] = page.Text.Length, ["ocr_used"] = page.OcrUsed, ["ocr_confidence"] = page.OcrConfidence,
                    ["text"] = sample
                });
            }
            data["preview_pages"] = preview;

            if (!writeBool)
            {
                return XpTask.FromResult(ToolResult.Success(data));
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("# PDF 文本提取：").Append(OutputSanitizer.MarkdownCell(path)).Append("\n\n");
            sb.Append("- 文档总页数：").Append(result.PageCount).Append("\n");
            sb.Append("- 本次提取范围：第 1~").Append(result.Pages.Count).Append(" 页\n");
            sb.Append("- 提取字符数：").Append(result.TotalChars).Append("\n");
            sb.Append("- 识别说明：").Append(result.OcrNotice).Append("\n");
            sb.Append("- 生成时间（UTC）：").Append(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"))
              .Append("\n\n");
            foreach (var page in result.Pages)
            {
                sb.Append("## 第 ").Append(page.PageNumber).Append(" 页\n\n");
                sb.Append(page.Text.Trim()).Append("\n\n");
            }
            var fileName = System.IO.Path.GetFileNameWithoutExtension(path) + "-text.md";
            var artifacts = TaskOutputs.CommitFiles(_policy, context.TaskId,
                new[] { (fileName, TaskOutputs.Utf8WithBom(sb.ToString()), "extracted_text_md") });
            data["artifacts"] = artifacts.Select(a => a.Path).ToList();
            return XpTask.FromResult(ToolResult.Success(data, artifacts));
        }
    }

    /// <summary>preview_table：CSV/XLSX 通用预览（表头 + 行 + 编码/格式信息）。只读。</summary>
    public sealed class PreviewTableTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;

        public PreviewTableTool(WorkspacePolicy policy) => _policy = policy;

        protected override string FallbackErrorCode => ErrorCodes.CsvInvalid;

        public override ToolDefinition Definition { get; } = new ToolDefinition(
            "preview_table",
            "预览 CSV 或 XLSX 表格：返回列名、最多 max_rows 行数据预览与格式信息。只读。",
            Schema(("path", "string", "表格文件的工作区相对路径（inputs/ 下）", true),
                   ("max_rows", "integer", "预览行数，范围 1~20", true)));

        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken)
        {
            ToolArgs.RejectUnknown(args, "path", "max_rows");
            var path = ToolArgs.RequireString(args, "path");
            var maxRows = ToolArgs.RequireIntInRange(args, "max_rows", 1, 20);
            if (!TableReading.IsSupported(path))
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat,
                    "不支持的表格格式（仅 .csv/.xlsx）：" + path);
            }

            var full = _policy.ResolveReadablePath(path);
            var table = TableReading.Read(full, path);

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

            return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?>
            {
                ["path"] = path,
                ["columns"] = new List<string>(table.Header),
                ["total_data_rows"] = table.Rows.Count,
                ["encoding"] = table.EncodingName,
                ["preview_rows"] = preview
            }));
        }
    }
}
