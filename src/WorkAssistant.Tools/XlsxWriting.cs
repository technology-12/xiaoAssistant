using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>表格单元格：字符串或数值（数值以原生 &lt;v&gt; 写入，Excel 中可直接参与计算）。</summary>
    public readonly struct XlsxCell
    {
        public string Text { get; }
        public bool IsNumeric { get; }

        public XlsxCell(string text, bool isNumeric)
        {
            Text = text;
            IsNumeric = isNumeric;
        }

        public static XlsxCell Text2(string text) => new(text, false);
        public static XlsxCell Number(decimal value) =>
            new(value.ToString("0.##", CultureInfo.InvariantCulture), true);
    }

    /// <summary>
    /// 最小 XLSX 写入器：单工作表、内联字符串、表头加粗。
    /// 部件仅含 [Content_Types]、rels、workbook、styles、sheet1 —— 不写宏与外部链接。
    /// 输出为确定性 ZIP（同输入同字节）。
    /// </summary>
    public static class XlsxWriting
    {
        public static byte[] Build(string sheetName, IList<string> header,
            IList<IList<XlsxCell>> rows)
        {
            if (header.Count == 0)
            {
                throw new ToolException(ErrorCodes.InvalidArguments, "表头为空，无法生成 XLSX。");
            }
            var safeSheet = SanitizeSheetName(sheetName);
            var entries = new List<KeyValuePair<string, byte[]>>
            {
                new("[Content_Types].xml", PackageZip.Utf8(BuildContentTypes())),
                new("_rels/.rels", PackageZip.Utf8(BuildRootRels())),
                new("xl/workbook.xml", PackageZip.Utf8(BuildWorkbook(safeSheet))),
                new("xl/_rels/workbook.xml.rels", PackageZip.Utf8(BuildWorkbookRels())),
                new("xl/styles.xml", PackageZip.Utf8(BuildStyles())),
                new("xl/worksheets/sheet1.xml", PackageZip.Utf8(BuildSheet(header, rows)))
            };
            return PackageZip.WriteDeterministic(entries);
        }

        private static string SanitizeSheetName(string name)
        {
            var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
            foreach (var c in invalid) name = name.Replace(c, '_');
            name = name.Trim();
            if (name.Length == 0) name = "Sheet1";
            if (name.Length > 31) name = name.Substring(0, 31);
            return name;
        }

        private static string BuildContentTypes() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "</Types>";

        private static string BuildRootRels() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        private static string BuildWorkbook(string sheetName) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"" + XmlEscape(sheetName) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
            "</workbook>";

        private static string BuildWorkbookRels() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>";

        /// <summary>最小样式表：0=默认，1=表头加粗。fills 前两项为规范要求的保留项。</summary>
        private static string BuildStyles() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\">" +
            "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            "</fonts>" +
            "<fills count=\"2\">" +
            "<fill><patternFill patternType=\"none\"/></fill>" +
            "<fill><patternFill patternType=\"gray125\"/></fill>" +
            "</fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"2\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
            "</cellXfs>" +
            "</styleSheet>";

        private static string BuildSheet(IList<string> header,
            IList<IList<XlsxCell>> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetData>");

            AppendRow(sb, 1, 0, header.Count, null, HeaderCells(header));
            for (var r = 0; r < rows.Count; r++)
            {
                AppendRow(sb, r + 2, r + 1, header.Count, rows[r], null);
            }

            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static XlsxCell[] HeaderCells(IList<string> header)
        {
            var cells = new XlsxCell[header.Count];
            for (var i = 0; i < header.Count; i++) cells[i] = XlsxCell.Text2(header[i]);
            return cells;
        }

        private static void AppendRow(StringBuilder sb, int rowNumber, int styleIndex,
            int columnCount, IList<XlsxCell>? cells, XlsxCell[]? headerCells)
        {
            sb.Append("<row r=\"").Append(rowNumber).Append("\">");
            var count = headerCells?.Length ?? cells!.Count;
            for (var c = 0; c < count && c < columnCount; c++)
            {
                var cell = headerCells != null ? headerCells[c] : cells![c];
                var reference = ColumnLetter(c) + rowNumber;
                sb.Append("<c r=\"").Append(reference).Append("\"");
                if (headerCells != null)
                {
                    sb.Append(" s=\"1\""); // 表头样式
                }
                if (cell.IsNumeric)
                {
                    sb.Append("><v>").Append(cell.Text).Append("</v></c>");
                }
                else
                {
                    sb.Append(" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                      .Append(XmlEscape(cell.Text)).Append("</t></is></c>");
                }
            }
            sb.Append("</row>");
        }

        internal static string ColumnLetter(int index)
        {
            var letters = new StringBuilder();
            index++;
            while (index > 0)
            {
                var rem = (index - 1) % 26;
                letters.Insert(0, (char)('A' + rem));
                index = (index - 1) / 26;
            }
            return letters.ToString();
        }

        internal static string XmlEscape(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                switch (c)
                {
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '&': sb.Append("&amp;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&apos;"); break;
                    default:
                        if (c < 0x20 && c != 0x9 && c != 0xA && c != 0xD)
                        {
                            sb.Append(' '); // 非法控制字符替换为空格
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
