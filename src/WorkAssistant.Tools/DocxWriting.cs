using System.Xml.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>
    /// 最小 DOCX 生成（正式版 §8.3：模板字段、表格与受控段落）：
    /// - 内容 = 标题 + 段落 + 聚合表格；不接受模型生成的任意 XML（只有纯文本进入文档）。
    /// - 模板填充：{{key}} 占位符替换（键拆分到多个 run 时报错列出，不静默）。
    /// - 确定性输出；不写宏、外部链接或嵌入对象。
    /// </summary>
    public static class DocxWriting
    {
        public static byte[] BuildReport(DocxDocument document)
        {
            var entries = new List<KeyValuePair<string, byte[]>>
            {
                new("[Content_Types].xml", PackageZip.Utf8(BuildContentTypes())),
                new("_rels/.rels", PackageZip.Utf8(BuildRootRels())),
                new("word/document.xml", PackageZip.Utf8(BuildDocumentXml(document))),
                new("word/_rels/document.xml.rels", PackageZip.Utf8(BuildDocumentRels())),
                new("word/styles.xml", PackageZip.Utf8(BuildStyles()))
            };
            return PackageZip.WriteDeterministic(entries);
        }

        /// <summary>模板填充：读取模板 docx，替换 word/document.xml 中的 {{key}}。</summary>
        public static byte[] FillTemplate(string templateFullPath, string relativePathForError,
            IDictionary<string, string> fields)
        {
            PackageZip.AssertZipOpenable(templateFullPath, relativePathForError);
            PackageZip.ValidatePassiveTemplate(templateFullPath);
            var documentXml = PackageZip.ReadEntry(templateFullPath, "word/document.xml");
            if (documentXml == null)
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat,
                    "模板缺少 word/document.xml（不是有效的 DOCX）：" + relativePathForError);
            }
            using var input = new System.IO.MemoryStream(documentXml);
            using var reader = DocumentLimits.Xml(input);
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var paragraph in document.Descendants(w + "p"))
            {
                DocumentLimits.Check();
                var nodes = paragraph.Descendants(w + "t").ToList();
                var joined = string.Concat(nodes.Select(n => n.Value));
                var matches = Regex.Matches(joined, @"\{\{([^{}]{1,64})\}\}").Cast<Match>().Reverse();
                foreach (var match in matches)
                {
                    string key = match.Groups[1].Value;
                    if (!fields.TryGetValue(key, out string value)) throw new ToolException(ErrorCodes.InvalidArguments, "模板存在未提供的字段：" + key);
                    found.Add(key);
                    int offset = 0, first = -1, last = -1, start = 0, end = 0;
                    for (int i = 0; i < nodes.Count; i++)
                    {
                        int next = offset + nodes[i].Value.Length;
                        if (first < 0 && match.Index < next) { first = i; start = match.Index - offset; }
                        if (match.Index + match.Length <= next) { last = i; end = match.Index + match.Length - offset; break; }
                        offset = next;
                    }
                    if (first < 0 || last < first) throw new ToolException(ErrorCodes.UnsupportedFormat, "模板字段的文本节点无法定位。");
                    string prefix = nodes[first].Value.Substring(0, start), suffix = nodes[last].Value.Substring(end);
                    nodes[first].Value = prefix + OutputSanitizer.PlainText(value) + (last == first ? suffix : "");
                    nodes[first].SetAttributeValue(XNamespace.Xml + "space", "preserve");
                    for (int i = first + 1; i < last; i++) nodes[i].Value = "";
                    if (last != first) { nodes[last].Value = suffix; nodes[last].SetAttributeValue(XNamespace.Xml + "space", "preserve"); }
                }
            }
            var missing = fields.Keys.Where(k => !found.Contains(k)).ToList();
            if (missing.Count > 0) throw new ToolException(ErrorCodes.InvalidArguments, "模板中未找到字段：" + string.Join(", ", missing));
            var replaced = document.ToString(SaveOptions.DisableFormatting);

            var entries = new List<KeyValuePair<string, byte[]>>();
            using (var archive = System.IO.Compression.ZipFile.OpenRead(templateFullPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName == "word/document.xml") continue;
                    using var stream = PackageZip.OpenEntry(entry);
                    using var buffer = new System.IO.MemoryStream();
                    stream.CopyTo(buffer);
                    entries.Add(new KeyValuePair<string, byte[]>(entry.FullName, buffer.ToArray()));
                }
            }
            // document.xml 放在原位（位置不影响打开，但保持与模板一致的顺序习惯）。
            entries.Insert(0, new KeyValuePair<string, byte[]>("word/document.xml", PackageZip.Utf8(replaced)));
            return PackageZip.WriteDeterministic(entries);
        }

        public static IList<string> TemplateFields(string fullPath)
        {
            PackageZip.ValidatePassiveTemplate(fullPath);
            var bytes = PackageZip.ReadEntry(fullPath, "word/document.xml") ?? throw new ToolException(ErrorCodes.UnsupportedFormat, "模板缺少主部件。");
            using var input = new System.IO.MemoryStream(bytes); using var reader = DocumentLimits.Xml(input);
            var document = XDocument.Load(reader);
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            return document.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value)))
                .SelectMany(text => Regex.Matches(text, @"\{\{([^{}]{1,64})\}\}").Cast<Match>().Select(m => m.Groups[1].Value)).Distinct(StringComparer.Ordinal).ToArray();
        }

        // ---- 部件模板 -------------------------------------------------------

        private static string BuildContentTypes() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            "</Types>";

        private static string BuildRootRels() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>";

        private static string BuildDocumentRels() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>";

        private static string BuildStyles() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:docDefaults><w:rPrDefault><w:rPr>" +
            "<w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:eastAsia=\"Microsoft YaHei\"/>" +
            "<w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/>" +
            "</w:rPr></w:rPrDefault></w:docDefaults>" +
            "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading1\"><w:name w:val=\"heading 1\"/>" +
            "<w:basedOn w:val=\"Normal\"/><w:pPr><w:outlineLvl w:val=\"0\"/></w:pPr>" +
            "<w:rPr><w:b/><w:sz w:val=\"36\"/><w:szCs w:val=\"36\"/></w:rPr></w:style>" +
            "<w:style w:type=\"paragraph\" w:styleId=\"Heading2\"><w:name w:val=\"heading 2\"/>" +
            "<w:basedOn w:val=\"Normal\"/><w:pPr><w:outlineLvl w:val=\"1\"/></w:pPr>" +
            "<w:rPr><w:b/><w:sz w:val=\"28\"/><w:szCs w:val=\"28\"/></w:rPr></w:style>" +
            "</w:styles>";

        // ---- document.xml 组装 ------------------------------------------------

        private static string BuildDocumentXml(DocxDocument doc)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">");
            sb.Append("<w:body>");

            if (doc.Title.Length > 0)
            {
                sb.Append(Paragraph(doc.Title, "Heading1"));
            }
            foreach (var block in doc.Blocks)
            {
                switch (block)
                {
                    case DocxHeading h:
                        sb.Append(Paragraph(h.Text, "Heading2"));
                        break;
                    case DocxParagraph p:
                        sb.Append(Paragraph(p.Text, null));
                        break;
                    case DocxBullet b:
                        sb.Append("<w:p><w:pPr><w:ind w:left=\"420\"/></w:pPr>");
                        sb.Append(Run("• " + b.Text));
                        sb.Append("</w:p>");
                        break;
                    case DocxTable t:
                        sb.Append(BuildTable(t));
                        break;
                }
            }

            // sectPr 为正文结尾必需。
            sb.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/>" +
                      "<w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/></w:sectPr>");
            sb.Append("</w:body></w:document>");
            return sb.ToString();
        }

        private static string Paragraph(string text, string? styleId)
        {
            var sb = new StringBuilder();
            sb.Append("<w:p>");
            if (styleId != null)
            {
                sb.Append("<w:pPr><w:pStyle w:val=\"").Append(styleId).Append("\"/></w:pPr>");
            }
            sb.Append(Run(text));
            sb.Append("</w:p>");
            return sb.ToString();
        }

        private static string Run(string text) =>
            "<w:r><w:t xml:space=\"preserve\">" + XmlTextEscape(text) + "</w:t></w:r>";

        private static string BuildTable(DocxTable table)
        {
            var sb = new StringBuilder();
            sb.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/>");
            sb.Append("<w:tblBorders>" +
                      "<w:top w:val=\"single\" w:sz=\"4\" w:color=\"999999\"/>" +
                      "<w:left w:val=\"single\" w:sz=\"4\" w:color=\"999999\"/>" +
                      "<w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"999999\"/>" +
                      "<w:right w:val=\"single\" w:sz=\"4\" w:color=\"999999\"/>" +
                      "<w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"999999\"/>" +
                      "<w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"999999\"/>" +
                      "</w:tblBorders></w:tblPr>");

            foreach (var row in table.Rows)
            {
                sb.Append("<w:tr>");
                foreach (var cell in row.Cells)
                {
                    sb.Append("<w:tc><w:tcPr/>");
                    sb.Append(Paragraph(cell, null));
                    sb.Append("</w:tc>");
                }
                sb.Append("</w:tr>");
            }
            sb.Append("</w:tbl>");
            // 表格后的空段（Word 兼容）。
            sb.Append(Paragraph("", null));
            return sb.ToString();
        }

        private static string XmlTextEscape(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '&': sb.Append("&amp;"); break;
                    default:
                        if (c < 0x20 && c != 0x9 && c != 0xA && c != 0xD) sb.Append(' ');
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    // ---- 受控文档模型（只有这些块能进入 DOCX）--------------------------------

    public abstract class DocxBlock
    {
        internal DocxBlock() { }
    }

    public sealed class DocxHeading : DocxBlock
    {
        public string Text { get; }
        public DocxHeading(string text) => Text = text;
    }

    public sealed class DocxParagraph : DocxBlock
    {
        public string Text { get; }
        public DocxParagraph(string text) => Text = text;
    }

    public sealed class DocxBullet : DocxBlock
    {
        public string Text { get; }
        public DocxBullet(string text) => Text = text;
    }

    public sealed class DocxTableRow
    {
        public IList<string> Cells { get; }
        public DocxTableRow(IList<string> cells) => Cells = cells;
    }

    public sealed class DocxTable : DocxBlock
    {
        public IList<DocxTableRow> Rows { get; }
        public DocxTable(IList<DocxTableRow> rows) => Rows = rows;
    }

    public sealed class DocxDocument
    {
        public string Title { get; }
        public IList<DocxBlock> Blocks { get; }

        public DocxDocument(string title, IList<DocxBlock> blocks)
        {
            Title = title;
            Blocks = blocks;
        }
    }
}
