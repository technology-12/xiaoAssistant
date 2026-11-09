using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    internal sealed class DocumentTextContent
    {
        public string Text { get; set; } = "";
        public string Scope { get; set; } = "";
        public bool PartialDocument { get; set; }
        public string[] Lines => Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
    }
    internal static class DocumentTextReader
    {
        public static DocumentTextContent Read(WorkspacePolicy policy, string relative, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string full = policy.ResolveReadablePath(relative), extension = Path.GetExtension(relative).ToLowerInvariant();
            var result = new DocumentTextContent();
            if (extension == ".pdf")
            {
                var pdf = PdfText.Extract(full, relative, 200);
                if (pdf.TotalChars == 0) throw new ToolException(ErrorCodes.UnsupportedFormat,
                    OfficeCapabilities.LocalOcrSupported ? "PDF 提取与本地 OCR 均未识别出文字；请检查扫描件清晰度或是否为空白页。"
                        : "PDF 未识别出文字；本版本不支持扫描件 OCR，请提供文本型 PDF 或人工核对原页。");
                result.Text = string.Join("\n", pdf.Pages.Select(p => "[第 " + p.PageNumber + " 页]\n" + p.Text));
                result.PartialDocument = pdf.Pages.Count < pdf.PageCount;
                result.Scope = "PDF 第 1~" + pdf.Pages.Count + " 页，共 " + pdf.PageCount + " 页；保留页码。" + pdf.OcrNotice;
            }
            else if (extension == ".pptx")
            {
                var parts = OfficePackage.Read(full); var slides = OfficePackage.SlideParts(parts);
                result.Text = string.Join("\n\n", slides.Select((slide, i) => "[第 " + (i + 1) + " 页]\n" + string.Join("\n", OfficePackage.Part(parts, slide).Descendants(OfficePackage.A + "p").Select(p => OfficePackage.Text(p, OfficePackage.A)))));
                result.Scope = "PPTX 普通文本形状，保留页码；不含图表、SmartArt、图片文字、备注或 OCR。";
            }
            else if (extension == ".docx")
            {
                if (new FileInfo(full).Length > 20L * 1024 * 1024) throw new ToolException(ErrorCodes.LimitExceeded, "DOCX 超过 20 MiB。");
                var bytes = PackageZip.ReadEntry(full, "word/document.xml") ?? throw new ToolException(ErrorCodes.UnsupportedFormat, "DOCX 缺少正文部件。");
                using var stream = new MemoryStream(bytes, false);
                using var reader = DocumentLimits.Xml(stream);
                var text = new StringBuilder();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == "http://schemas.openxmlformats.org/wordprocessingml/2006/main")
                    {
                        if (reader.LocalName == "t")
                        {
                            using var subtree = reader.ReadSubtree();
                            while (subtree.Read()) if (subtree.NodeType == XmlNodeType.Text || subtree.NodeType == XmlNodeType.SignificantWhitespace) text.Append(subtree.Value);
                        }
                        else if (reader.LocalName == "tab") text.Append('\t');
                        else if (reader.LocalName == "br") text.Append('\n');
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "p") text.Append('\n');
                    if (text.Length > 2000000) throw new ToolException(ErrorCodes.LimitExceeded, "文档正文超过本地200万字符资源预算。");
                }
                result.Text = text.ToString(); result.Scope = "DOCX 正文及表格段落；不含页眉、页脚、批注、图像或嵌入对象。";
            }
            else
            {
                if (!new[] { ".txt", ".md", ".json", ".log", ".xml", ".csv" }.Contains(extension))
                    throw new ToolException(ErrorCodes.UnsupportedFormat, "文本读取支持 DOCX/PDF/TXT/MD/JSON/LOG/XML/CSV；XLSX 请使用 preview_table。");
                if (new FileInfo(full).Length > 2L * 1024 * 1024) throw new ToolException(ErrorCodes.LimitExceeded, "文本文件超过 2 MiB。");
                try { using var reader = new StreamReader(full, new UTF8Encoding(false, true), true); result.Text = reader.ReadToEnd(); result.Scope = "文本文件，UTF-8 或 BOM 指定编码。"; }
                catch (DecoderFallbackException)
                {
                    result.Text = File.ReadAllText(full, Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback));
                    result.Scope = "文本文件，UTF-8 校验失败后按 GB18030 解码；如文字异常请转换为 UTF-8。";
                }
                if (result.Text.IndexOf('\0') >= 0) throw new ToolException(ErrorCodes.UnsupportedFormat, "输入包含二进制内容，不能作为普通文本读取。");
            }
            if (result.Text.Length > 2000000) throw new ToolException(ErrorCodes.LimitExceeded, "文档文本超过本地200万字符资源预算。");
            return result;
        }
    }
    public sealed class ReadDocumentTextTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;
        public ReadDocumentTextTool(WorkspacePolicy policy) { _policy = policy; }
        protected override string FallbackErrorCode => ErrorCodes.UnsupportedFormat;
        public override ToolDefinition Definition { get; } = new("read_document_text", "按行分页读取授权文档正文，支持 Word、PPT、PDF" + (OfficeCapabilities.LocalOcrSupported ? "（扫描页自动本地 OCR，结果须核对原页）" : "") + "和文本文件。返回实际范围及后续位置；不要把一页内容当全文。仅返回内容，不写成果。",
            Schema(("path", "string", "输入文件引用", true), ("start_line", "integer", "起始行，从 1 开始，默认 1", false), ("max_lines", "integer", "最多 1~100 行，默认 40", false), ("start_char", "integer", "超长行字符偏移，默认0；按 next_line/next_char 继续", false)));
        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context, JObject args, CancellationToken token)
        {
            ToolArgs.RejectUnknown(args, "path", "start_line", "max_lines", "start_char");
            string path = ToolArgs.RequireString(args, "path");
            int start = ToolArgs.OptionalIntInRange(args, "start_line", 1, 1, 500001), count = ToolArgs.OptionalIntInRange(args, "max_lines", 40, 1, 100);
            var document = DocumentTextReader.Read(_policy, path, token); var lines = document.Lines;
            if (start > lines.Length) throw new ToolException(ErrorCodes.InvalidArguments, "start_line 超出文档行数。");
            var page = new JArray(); int chars = 0, index = start - 1;
            int offset = ToolArgs.OptionalIntInRange(args, "start_char", 0, 0, 2000000), nextChar = 0;
            while (index < lines.Length && page.Count < count)
            {
                int from = page.Count == 0 ? offset : 0;
                if (from > lines[index].Length || from > 0 && from < lines[index].Length && char.IsLowSurrogate(lines[index][from])) throw new ToolException(ErrorCodes.InvalidArguments, "字符偏移无效，请使用上一页 next_char。");
                int length = Math.Min(lines[index].Length - from, 20000 - chars);
                if (length == 0 && from < lines[index].Length) break;
                if (from + length < lines[index].Length && length > 0 && char.IsHighSurrogate(lines[index][from + length - 1])) length--;
                bool partial = from + length < lines[index].Length;
                page.Add(new JObject { ["line"] = index + 1, ["text"] = lines[index].Substring(from, length), ["start_char"] = from, ["total_chars"] = lines[index].Length, ["partial_text"] = from > 0 || partial }); chars += length;
                if (partial) { nextChar = from + length; break; } index++;
            }
            DocumentWorkspaceTool.RecordRead(_policy, context.TaskId, path, "text", start, offset, index + 1, nextChar, index < lines.Length, document.PartialDocument);
            return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["path"] = path, ["lines"] = page, ["total_lines"] = lines.Length,
                ["next_line"] = index + 1, ["next_char"] = nextChar, ["has_more"] = index < lines.Length, ["partial_document"] = document.PartialDocument, ["scope_notice"] = document.Scope }));
        }
    }
    public sealed class SearchInputTextTool : AgentToolBase
    {
        private readonly WorkspacePolicy _policy;
        public SearchInputTextTool(WorkspacePolicy policy) { _policy = policy; }
        protected override string FallbackErrorCode => ErrorCodes.UnsupportedFormat;
        public override ToolDefinition Definition { get; } = new("search_input_text", "在最多 10 个授权文档正文中搜索字面文本，返回行号和附近片段；每行只返回第一处匹配。支持 Word、PDF" + (OfficeCapabilities.LocalOcrSupported ? "（扫描页自动本地 OCR）" : "") + "、文本文件，不运行正则或外部命令。",
            Schema(("paths", "array", "文件引用数组，最多 10 个", true), ("query", "string", "要搜索的文本，1~200 字符，默认忽略大小写", true)));
        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context, JObject args, CancellationToken token)
        {
            ToolArgs.RejectUnknown(args, "paths", "query");
            var paths = ToolArgs.RequireStringArray(args, "paths", 10); string query = ToolArgs.RequireString(args, "query");
            if (query.Length > 200) throw new ToolException(ErrorCodes.InvalidArguments, "搜索文本最多 200 字符。");
            var matches = new JArray(); var scopes = new JArray(); int total = 0;
            foreach (var path in paths.Distinct(StringComparer.Ordinal))
            {
                var document = DocumentTextReader.Read(_policy, path, token); var lines = document.Lines;
                scopes.Add(new JObject { ["path"] = path, ["scope_notice"] = document.Scope, ["partial_document"] = document.PartialDocument });
                for (int i = 0; i < lines.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    int found = lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase);
                    if (found < 0) continue;
                    total++;
                    if (matches.Count >= 50) continue;
                    int from = Math.Max(0, found - 60);
                    matches.Add(new JObject { ["path"] = path, ["line"] = i + 1, ["column"] = found + 1,
                        ["snippet"] = lines[i].Substring(from, Math.Min(300, lines[i].Length - from)), ["snippet_is_partial"] = from > 0 || lines[i].Length > 300 });
                }
            }
            return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["matches"] = matches, ["matched_lines"] = total, ["has_more_matches"] = total > matches.Count, ["scopes"] = scopes }));
        }
    }
}
