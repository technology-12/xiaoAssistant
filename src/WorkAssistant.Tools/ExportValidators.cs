using System;
using System.Linq;
using System.Xml.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public static class ExportValidators
    {
        public static void AggregateTable(string fullPath, AggregationResult expected, bool xlsx)
        {
            var table = xlsx ? XlsxReading.ReadFirstSheet(fullPath, "summary.xlsx") : CsvReading.Read(fullPath, "summary.csv");
            string Text(string value) => xlsx ? XElement.Parse("<t>" + XlsxWriting.XmlEscape(value) + "</t>").Value : OutputSanitizer.CsvCell(value);
            if (!table.Header.SequenceEqual(new[] { Text(expected.GroupByColumn), Text(expected.SumColumn) }) || table.Rows.Count != expected.Groups.Count)
                throw new ToolException(ErrorCodes.OutputWriteFailed, "汇总表重新解析后，列名或分组数不一致。");
            for (int i = 0; i < expected.Groups.Count; i++)
            {
                DocumentLimits.Check();
                var row = table.Rows[i]; var group = expected.Groups[i];
                if (row.Length != 2 || row[0] != Text(group.Key) || AmountParser.TryParse(row[1]) != group.Value)
                    throw new ToolException(ErrorCodes.OutputWriteFailed, "汇总表重新解析后，分组或金额不一致。");
            }
        }
    }
}
