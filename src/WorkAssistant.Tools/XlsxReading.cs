using System.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>
    /// 最小 XLSX 读取器（正式版 §8.2）：
    /// - 读取第一个工作表；支持共享字符串、内联字符串、公式缓存值、数值与布尔。
    /// - 单元格以原始值（invariant 字符串）进入表格管线；日期按样式和工作簿日历转换为 ISO 格式。
    /// - 宏/外部链接/嵌入对象不加载（只读声明的三个 XML 部件）。
    /// - 纯托管解析，无 native 依赖；文件大小与行列数沿用 CSV 限制。
    /// </summary>
    public static class XlsxReading
    {
        /// <summary>读取第一个工作表为表格（表头 = 第一行）。</summary>
        public static CsvTable ReadFirstSheet(string fullPath, string relativePathForError)
        {
            if (!File.Exists(fullPath))
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "文件不存在：" + relativePathForError);
            }
            var info = new FileInfo(fullPath);
            if (info.Length > CsvReading.MaxFileBytes)
            {
                throw new ToolException(ErrorCodes.LimitExceeded,
                    "文件超过 10 MiB 限制：" + relativePathForError);
            }

            PackageZip.AssertZipOpenable(fullPath, relativePathForError);
            PackageZip.ValidatePassiveTemplate(fullPath);
            using var archive = OpenArchive(fullPath, relativePathForError);
            PackageZip.ValidateArchive(archive);
            var shared = ReadSharedStrings(archive, relativePathForError);
            var sheetPath = ResolveFirstSheetPath(archive, relativePathForError);
            var (header, rows) = ReadSheet(archive, sheetPath, shared, relativePathForError);

            if (header.Length == 0)
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat,
                    "工作表为空：" + relativePathForError);
            }
            return new CsvTable(header, rows, "xlsx", info.Length);
        }

        // ---- 部件定位 -------------------------------------------------------

        private static ZipArchive OpenArchive(string fullPath, string rel)
        {
            try
            {
                return ZipFile.OpenRead(fullPath);
            }
            catch (InvalidDataException)
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat,
                    "不是有效的 XLSX 文件（可能为旧版 .xls 或已损坏）：" + rel);
            }
        }

        private static string ResolveFirstSheetPath(ZipArchive archive, string rel)
        {
            var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
            var workbookEntry = archive.GetEntry("xl/workbook.xml");
            if (relsEntry == null || workbookEntry == null)
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat,
                    "缺少工作簿部件（可能不是标准 XLSX）：" + rel);
            }
            // 解析 workbook.xml 第一个 sheet 的 r:id，再映射到 rels Target。
            var relId = ReadFirstSheetRelId(workbookEntry);
            var target = relId == null ? null : ReadRelTarget(relsEntry, relId);
            if (target != null)
            {
                var normalized = target.StartsWith("/", StringComparison.Ordinal)
                    ? target.TrimStart('/')
                    : "xl/" + target;
                if (archive.GetEntry(normalized) != null) return normalized;
            }
            throw new ToolException(ErrorCodes.UnsupportedFormat,
                "找不到工作表部件：" + rel);
        }

        private static string? ReadFirstSheetRelId(ZipArchiveEntry workbookEntry)
        {
            try
            {
                using var stream = PackageZip.OpenEntry(workbookEntry);
                using var reader = DocumentLimits.Xml(stream);
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element &&
                        reader.LocalName == "sheet")
                    {
                        return reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    }
                }
            }
            catch (XmlException)
            {
                // 结构异常时走回退路径。
            }
            return null;
        }

        private static string? ReadRelTarget(ZipArchiveEntry relsEntry, string relId)
        {
            try
            {
                using var stream = PackageZip.OpenEntry(relsEntry);
                using var reader = DocumentLimits.Xml(stream);
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element &&
                        reader.LocalName == "Relationship" &&
                        reader.GetAttribute("Id") == relId)
                    {
                        return reader.GetAttribute("Target");
                    }
                }
            }
            catch (XmlException)
            {
            }
            return null;
        }

        // ---- 共享字符串 -------------------------------------------------------

        private static List<string> ReadSharedStrings(ZipArchive archive, string rel)
        {
            var result = new List<string>();
            var entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null) return result;
            try
            {
                using var stream = PackageZip.OpenEntry(entry);
                using var reader = DocumentLimits.Xml(stream);
                var doc = System.Xml.Linq.XDocument.Load(reader);
                var ns = doc.Root!.Name.Namespace;
                foreach (var item in doc.Root.Elements(ns + "si"))
                {
                    DocumentLimits.Check();
                    string text = string.Concat(item.Descendants(ns + "t").Where(t => !t.Ancestors(ns + "rPh").Any()).Select(t => t.Value));
                    if (text.Length > 65536 || result.Count >= 200000) throw new ToolException(ErrorCodes.LimitExceeded, "共享字符串数量或长度超限。");
                    result.Add(text);
                }
            }
            catch (XmlException ex)
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat,
                    "共享字符串部件解析失败：" + rel + "；" + ex.Message);
            }
            return result;
        }

        // ---- 工作表解析 -------------------------------------------------------

        private static (string[] Header, List<string[]> Rows) ReadSheet(
            ZipArchive archive, string sheetPath, List<string> shared, string rel)
        {
            var entry = archive.GetEntry(sheetPath)!;
            var rows = new List<string[]>();
            string[]? header = null;
            var maxCols = 0;
            var dateStyles = ReadDateStyles(archive);
            var date1904 = Uses1904Dates(archive);

            try
            {
                using var stream = PackageZip.OpenEntry(entry);
                using var safeReader = DocumentLimits.Xml(stream);
                var doc = System.Xml.Linq.XDocument.Load(safeReader, System.Xml.Linq.LoadOptions.None);
                if (doc.Root == null)
                {
                    throw new ToolException(ErrorCodes.UnsupportedFormat,
                        "工作表 XML 为空：" + rel);
                }
                // worksheet 元素通常带默认命名空间；查询一律带上 Root 的命名空间。
                var ns = doc.Root.Name.Namespace;
                var sheetData = doc.Root.Element(ns + "sheetData");
                if (sheetData == null)
                {
                    throw new ToolException(ErrorCodes.UnsupportedFormat,
                        "工作表缺少 sheetData：" + rel);
                }
                foreach (var rowElement in sheetData.Elements(ns + "row"))
                {
                    DocumentLimits.Check();
                    var current = new List<string>();
                    foreach (var cellElement in rowElement.Elements(ns + "c"))
                    {
                        var refAttr = (string?)cellElement.Attribute("r");
                        var colIndex = refAttr == null ? current.Count : ColumnIndex(refAttr);
                        if (colIndex < current.Count) throw new ToolException(ErrorCodes.UnsupportedFormat, "单元格列号重复或未按列顺序保存：" + rel);
                        if (colIndex >= DocumentLimits.MaxColumns) throw new ToolException(ErrorCodes.LimitExceeded, "工作表列号超限。 ");
                        // 补齐跳过的空单元格，保持列对齐。
                        while (current.Count < colIndex) current.Add("");
                        current.Add(ReadCell(cellElement, shared, rel, dateStyles, date1904));
                    }
                    while (current.Count > 0 && current[current.Count - 1].Length == 0) current.RemoveAt(current.Count - 1);
                    if (current.Count == 0) continue;
                    if (header != null && current.Count > header.Length) throw new ToolException(ErrorCodes.UnsupportedFormat, "数据列超出首行表头范围：" + rel);
                    if (current.Count > maxCols) maxCols = current.Count;
                    while (current.Count < maxCols) current.Add("");
                    var cells = current.ToArray();
                    if (header == null)
                    {
                        header = cells;
                        if (header.Any(string.IsNullOrWhiteSpace) || header.Distinct(StringComparer.Ordinal).Count() != header.Length)
                            throw new ToolException(ErrorCodes.UnsupportedFormat, "首行表头为空或重复，请先整理成单行唯一列名：" + rel);
                    }
                    else
                    {
                        rows.Add(cells);
                        if (rows.Count > CsvReading.MaxDataRows)
                        {
                            throw new ToolException(ErrorCodes.LimitExceeded,
                                "超过最大数据行数限制：" + rel);
                        }
                    }
                }
            }
            catch (System.Xml.XmlException ex)
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat,
                    "工作表解析失败：" + rel + "；" + ex.Message);
            }

            header ??= XpArray.Empty<string>();
            return (header, rows);
        }

        /// <summary>读取单个 &lt;c&gt; 单元格：t=s 取共享串，inlineStr 取内联，否则取 &lt;v&gt;。</summary>
        private static string ReadCell(System.Xml.Linq.XElement cellElement,
            List<string> shared, string rel, HashSet<int> dateStyles, bool date1904)
        {
            var type = (string?)cellElement.Attribute("t");
            var valueElement = cellElement.Element(cellElement.Name.Namespace + "v");
            if (type == "inlineStr")
            {
                var inline = cellElement.Element(cellElement.Name.Namespace + "is");
                string text = inline == null ? "" : string.Concat(inline.Descendants(cellElement.Name.Namespace + "t")
                    .Where(t => !t.Ancestors(cellElement.Name.Namespace + "rPh").Any()).Select(t => t.Value));
                if (text.Length > 65536) throw new ToolException(ErrorCodes.LimitExceeded, "内联字符串超限。");
                return text;
            }
            if (cellElement.Element(cellElement.Name.Namespace + "f") != null && (valueElement == null || string.IsNullOrWhiteSpace(valueElement.Value)))
                throw new ToolException(ErrorCodes.UnsupportedFormat, "公式没有有效缓存，请先在 Excel/WPS 重算并保存：" + rel);
            if (valueElement == null)
            {
                if (cellElement.Element(cellElement.Name.Namespace + "f") != null)
                    throw new ToolException(ErrorCodes.UnsupportedFormat, "公式缺少缓存值，请先在 Excel/WPS 重算并保存：" + rel);
                return "";
            }
            if (valueElement.Value.Length > 65536) throw new ToolException(ErrorCodes.LimitExceeded, "单元格内容超限。 ");
            var style = (int?)cellElement.Attribute("s") ?? 0;
            if (type != "s" && type != "inlineStr" && dateStyles.Contains(style) &&
                double.TryParse(valueElement.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial))
            {
                if (!date1904 && serial >= 60 && serial < 61)
                    throw new ToolException(ErrorCodes.UnsupportedFormat, "Excel 虚构的 1900-02-29 日期无法可靠转换。 ");
                var oa = serial + (date1904 ? 1462 : serial < 60 ? 1 : 0);
                return DateTime.FromOADate(oa).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            return ConvertValue(type, valueElement.Value, shared, rel);
        }

        private static string ConvertValue(string? type, string raw, List<string> shared, string rel)
        {
            switch (type)
            {
                case "s":
                {
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) ||
                        index < 0 || index >= shared.Count)
                    {
                        throw new ToolException(ErrorCodes.UnsupportedFormat,
                            "共享字符串索引越界（" + raw + "）：" + rel);
                    }
                    return shared[index];
                }
                case "b":
                    return raw == "1" ? "TRUE" : "FALSE";
                case "str": // 公式缓存字符串
                case "inlineStr":
                    return raw;
                case "e": // 错误单元格（#DIV/0! 等）
                    return raw;
                default:
                    // 数值：保持原始 invariant 文本；日期为序列值（限制见类注释）。
                    return raw;
            }
        }

        private static bool Uses1904Dates(ZipArchive archive)
        {
            var entry = archive.GetEntry("xl/workbook.xml"); if (entry == null) return false;
            using var stream = PackageZip.OpenEntry(entry); using var reader = DocumentLimits.Xml(stream);
            while (reader.Read()) if (reader.LocalName == "workbookPr")
                return reader.GetAttribute("date1904") is "1" or "true";
            return false;
        }
        private static HashSet<int> ReadDateStyles(ZipArchive archive)
        {
            var result = new HashSet<int>(); var entry = archive.GetEntry("xl/styles.xml");
            if (entry == null) return result;
            using var stream = PackageZip.OpenEntry(entry); using var reader = DocumentLimits.Xml(stream);
            var doc = System.Xml.Linq.XDocument.Load(reader); var ns = doc.Root!.Name.Namespace;
            var formats = new HashSet<int>();
            foreach (var format in doc.Root.Element(ns + "numFmts")?.Elements(ns + "numFmt") ?? System.Linq.Enumerable.Empty<System.Xml.Linq.XElement>())
            {
                var code = (string?)format.Attribute("formatCode") ?? "";
                code = System.Text.RegularExpressions.Regex.Replace(code, @"""[^""]*""|\\.", "");
                if (System.Text.RegularExpressions.Regex.IsMatch(code, "[ydhs]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    formats.Add((int?)format.Attribute("numFmtId") ?? -1);
            }
            var index = 0;
            foreach (var xf in doc.Root.Element(ns + "cellXfs")?.Elements(ns + "xf") ?? System.Linq.Enumerable.Empty<System.Xml.Linq.XElement>())
            {
                var id = (int?)xf.Attribute("numFmtId") ?? 0;
                if ((id >= 14 && id <= 22) || (id >= 45 && id <= 47) || formats.Contains(id)) result.Add(index);
                index++;
            }
            return result;
        }
        /// <summary>"BC23" → 列序号（0 起）。</summary>
        internal static int ColumnIndex(string cellRef)
        {
            var index = 0;
            foreach (var c in cellRef)
            {
                if (c < 'A' || c > 'Z')
                {
                    if (c >= 'a' && c <= 'z')
                    {
                        index = checked(index * 26 + (c - 'a' + 1));
                        continue;
                    }
                    break;
                }
                index = checked(index * 26 + (c - 'A' + 1));
            }
            return Math.Max(0, index - 1);
        }
    }
}
