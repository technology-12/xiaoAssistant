using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public static class WordAutomation
    {
        public static List<DocxBlock> Validate(string title, JArray blocks, int maxChars = 1000000, int maxBlocks = 10000)
        {
            if (blocks.Count < 1 || blocks.Count > maxBlocks) throw new ToolException(ErrorCodes.LimitExceeded, "Word 正文块数超过当前文档资源预算：" + maxBlocks);
            var content = new List<DocxBlock>(); int total = title.Length;
            foreach (var item in blocks)
            {
                DocumentLimits.Check();
                if (item is not JObject block) throw new ToolException(ErrorCodes.InvalidArguments, "blocks 每项必须为对象。");
                ToolArgs.RejectUnknown(block, "type", "text", "rows");
                string type = OfficePackage.Field(block, "type", 20);
                if (type == "table")
                {
                    if (block["rows"] is not JArray rows || rows.Count < 1 || rows.Count > 80) throw new ToolException(ErrorCodes.InvalidArguments, "表格需要 1~80 行。");
                    int columns = -1; var table = new List<DocxTableRow>();
                    foreach (var value in rows)
                    {
                        if (value is not JArray cells || cells.Count < 1 || cells.Count > 6 || columns >= 0 && columns != cells.Count) throw new ToolException(ErrorCodes.InvalidArguments, "表格需为等宽的 1~6 列。");
                        columns = cells.Count;
                        var text = cells.Select(c => c.Type == JTokenType.String && ((string)c!).Length <= 1200 ? (string)c! : throw new ToolException(ErrorCodes.InvalidArguments, "表格单元格必须为 1200 字符以内的文本。")).ToArray();
                        total += text.Sum(s => s.Length); table.Add(new DocxTableRow(text));
                    }
                    content.Add(new DocxTable(table));
                }
                else
                {
                    string text = OfficePackage.Field(block, "text"); total += text.Length;
                    content.Add(type switch { "heading" => new DocxHeading(text), "paragraph" => new DocxParagraph(text), "bullet" => new DocxBullet(text), _ => throw new ToolException(ErrorCodes.InvalidArguments, "块类型支持 heading/paragraph/bullet/table。") });
                }
                if (total > maxChars) throw new ToolException(ErrorCodes.LimitExceeded, "文档超过当前字符预算：" + maxChars + "；草稿保留，可调整 .claude/document-limits.json。");
            }
            return content;
        }
        public static byte[] Create(string title, JArray blocks) => Create(title, blocks, 1000000, 10000);
        public static byte[] Create(string title, JArray blocks, int maxChars = 1000000, int maxBlocks = 10000)
        {
            var content = Validate(title, blocks, maxChars, maxBlocks);
            var bytes = DocxWriting.BuildReport(new DocxDocument(title, content));
            using var memory = new System.IO.MemoryStream(bytes); using var zip = new System.IO.Compression.ZipArchive(memory);
            var parts = zip.Entries.ToDictionary(e => e.FullName, e => { using var stream = e.Open(); return DocumentLimits.ReadBounded(stream); });
            var document = OfficePackage.Part(parts, "word/document.xml"); var w = OfficePackage.W;
            var paragraphs = OfficePackage.WordParagraphs(document);
            if (paragraphs.Count > 0) paragraphs[0].Element(w + "pPr")!.Element(w + "pStyle")!.SetAttributeValue(w + "val", "Title");
            foreach (var heading in paragraphs.Where(p => p.Element(w + "pPr")?.Element(w + "pStyle") != null)) heading.Element(w + "pPr")!.Add(new XElement(w + "keepNext"));
            foreach (var text in document.Descendants(w + "t").Where(t => t.Value.Contains('\n')).ToArray())
            {
                var values = text.Value.Replace("\r\n", "\n").Split('\n'); text.Value = values[0];
                XNode previous = text;
                foreach (var line in values.Skip(1)) { var br = new XElement(w + "br"); var next = new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), line); previous.AddAfterSelf(br, next); previous = next; }
            }
            foreach (var table in document.Descendants(w + "tbl"))
            {
                int cols = table.Elements(w + "tr").First().Elements(w + "tc").Count();
                table.Element(w + "tblPr")!.Add(new XElement(w + "tblCellMar", new[] { "top", "left", "bottom", "right" }.Select(side => new XElement(w + side, new XAttribute(w + "w", 80), new XAttribute(w + "type", "dxa")))));
                table.Elements(w + "tr").First().AddFirst(new XElement(w + "trPr", new XElement(w + "tblHeader")));
                table.Element(w + "tblPr")!.AddAfterSelf(new XElement(w + "tblGrid", Enumerable.Range(0, cols).Select(_ => new XElement(w + "gridCol", new XAttribute(w + "w", 9026 / cols)))));
                foreach (var cell in table.Elements(w + "tr").First().Elements(w + "tc")) cell.Element(w + "tcPr")!.Add(new XElement(w + "shd", new XAttribute(w + "fill", "EDF2EF")));
            }
            parts["word/document.xml"] = OfficePackage.Bytes(document);
            parts["word/_rels/document.xml.rels"] = PackageZip.Utf8("<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rIdStyles' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles' Target='styles.xml'/></Relationships>");
            parts["word/styles.xml"] = PackageZip.Utf8("<w:styles xmlns:w='" + w + "'><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii='Calibri' w:eastAsia='宋体'/><w:sz w:val='24'/><w:color w:val='18251E'/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after='120' w:line='360' w:lineRule='auto'/></w:pPr></w:pPrDefault></w:docDefaults><w:style w:type='paragraph' w:default='1' w:styleId='Normal'><w:name w:val='Normal'/></w:style><w:style w:type='paragraph' w:styleId='Title'><w:name w:val='Title'/><w:basedOn w:val='Normal'/><w:pPr><w:jc w:val='center'/><w:spacing w:after='320'/></w:pPr><w:rPr><w:b/><w:rFonts w:eastAsia='黑体'/><w:sz w:val='40'/><w:color w:val='000000'/></w:rPr></w:style><w:style w:type='paragraph' w:styleId='Heading2'><w:name w:val='heading 2'/><w:basedOn w:val='Normal'/><w:pPr><w:outlineLvl w:val='0'/><w:spacing w:before='220' w:after='140'/></w:pPr><w:rPr><w:b/><w:rFonts w:eastAsia='黑体'/><w:sz w:val='30'/></w:rPr></w:style></w:styles>");
            return OfficePackage.Pack(parts);
        }
        public static void Edit(Dictionary<string, byte[]> parts, JArray edits, bool tracked)
        {
            var document = OfficePackage.Part(parts, "word/document.xml"); var w = OfficePackage.W; var paragraphs = OfficePackage.WordParagraphs(document);
            var seen = new HashSet<int>(); int revision = document.Descendants().Where(e => e.Name == w + "ins" || e.Name == w + "del").Select(e => int.TryParse((string?)e.Attribute(w + "id"), out int id) ? id : 0).DefaultIfEmpty(0).Max();
            if (revision > 1000000) throw new ToolException(ErrorCodes.UnsupportedFormat, "修订编号超出支持范围。");
            foreach (var item in edits)
            {
                if (item is not JObject edit) throw new ToolException(ErrorCodes.InvalidArguments, "edits 每项需为对象。");
                ToolArgs.RejectUnknown(edit, "paragraph_index", "expected_text", "new_text");
                int index = ToolArgs.OptionalIntInRange(edit, "paragraph_index", 0, 1, paragraphs.Count);
                if (index < 1 || !seen.Add(index)) throw new ToolException(ErrorCodes.InvalidArguments, "段落编号无效或重复。");
                var paragraph = paragraphs[index - 1]; string before = OfficePackage.Field(edit, "expected_text", 4000, true), after = OfficePackage.Field(edit, "new_text", 4000, true);
                if (before != OfficePackage.Text(paragraph, w)) throw new ToolException(ErrorCodes.EditConflict, "段落内容已变化，不能套用旧修改。");
                if (after.Contains('\n') || after.Contains('\r') || after == before) throw new ToolException(ErrorCodes.InvalidArguments, "段落替换需为不同的单段文本，不含换行。");
                if (paragraph.Elements().Any(e => e.Name != w + "pPr" && e.Name != w + "r") || paragraph.Elements(w + "r").Any(r => r.Elements().Any(e => e.Name != w + "rPr" && e.Name != w + "t")))
                    throw new ToolException(ErrorCodes.UnsupportedFormat, "目标段落含域、图像、链接、已有修订或复杂控件；请选择纯文本段落。");
                var oldRuns = paragraph.Elements(w + "r").Select(r => new XElement(r)).ToArray(); var properties = oldRuns.FirstOrDefault()?.Element(w + "rPr");
                paragraph.Elements(w + "r").Remove();
                var run = new XElement(w + "r", properties == null ? null : new XElement(properties), new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), after));
                if (tracked)
                {
                    foreach (var text in oldRuns.SelectMany(r => r.Descendants(w + "t"))) text.Name = w + "delText";
                    paragraph.Add(new XElement(w + "del", new XAttribute(w + "id", ++revision), new XAttribute(w + "author", "晓助手"), oldRuns),
                        new XElement(w + "ins", new XAttribute(w + "id", ++revision), new XAttribute(w + "author", "晓助手"), run));
                }
                else paragraph.Add(run);
            }
            parts["word/document.xml"] = OfficePackage.Bytes(document);
        }
    }
}
