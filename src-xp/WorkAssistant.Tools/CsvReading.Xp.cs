// XP 变体（替换 src/WorkAssistant.Tools/CsvReading.cs）：
// CsvHelper 33.0.1 无 net40 资产，改用仓库自研 RFC 4180 解析器，
// 保持相同公开行为：引号/字段内逗号与换行、跳过空行、列数一致性校验、
// 单字段 64 KiB 与最大行数限制、编码检测（UTF-8 BOM / UTF-16 / 严格 UTF-8 / GB18030 回退）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>解析后的 CSV 表：表头 + 数据行（原始字符串）。</summary>
    public sealed class CsvTable
    {
        public string[] Header { get; }
        public List<string[]> Rows { get; }
        public string EncodingName { get; }
        public long FileBytes { get; }

        public CsvTable(string[] header, List<string[]> rows, string encodingName, long fileBytes)
        {
            Header = header;
            Rows = rows;
            EncodingName = encodingName;
            FileBytes = fileBytes;
        }
    }

    public static class CsvReading
    {
        public const long MaxFileBytes = 10L * 1024 * 1024;          // 单文件 10 MiB
        public const long MaxTotalBytes = 50L * 1024 * 1024;         // 每任务 50 MiB
        public const int MaxDataRows = 100_000;                       // 每任务 10 万行
        public const int MaxFieldChars = 64 * 1024;                   // 单字段 64 KiB

        public static CsvTable Read(string fullPath, string relativePathForError)
        {
            var (text, encodingName, fileBytes) = ReadText(fullPath, relativePathForError);
            return Parse(text, encodingName, fileBytes, relativePathForError, MaxDataRows);
        }

        public static Tuple<string, string, long> ReadTextForTest(string fullPath, string rel)
        {
            var t = ReadText(fullPath, rel);
            return Tuple.Create(t.text, t.encodingName, t.fileBytes);
        }

        private static (string text, string encodingName, long fileBytes) ReadText(
            string fullPath, string relativePathForError)
        {
            if (!File.Exists(fullPath))
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "文件不存在：" + relativePathForError);
            }
            var info = new FileInfo(fullPath);
            if (info.Length > MaxFileBytes)
            {
                throw new ToolException(ErrorCodes.LimitExceeded,
                    "文件超过 10 MiB 限制：" + relativePathForError);
            }
            var bytes = File.ReadAllBytes(fullPath);

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "utf-8-bom", info.Length);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), "utf-16le", info.Length);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), "utf-16be", info.Length);
            }

            // 严格 UTF-8 验证：任何无效序列都回退 GB18030。
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            try
            {
                return (strictUtf8.GetString(bytes), "utf-8", info.Length);
            }
            catch (DecoderFallbackException)
            {
                try
                {
                    var gb = Encoding.GetEncoding("GB18030");
                    return (gb.GetString(bytes), "gb18030", info.Length);
                }
                catch (Exception ex)
                {
                    throw new ToolException(ErrorCodes.CsvInvalid,
                        "无法解码文件（既非 UTF-8 也非 GB18030）：" + relativePathForError + "；" + ex.Message);
                }
            }
        }

        /// <summary>RFC 4180 解析：双引号包裹、字段内逗号/换行、"" 转义。</summary>
        public static CsvTable Parse(string text, string encodingName, long fileBytes,
            string relativePathForError, int maxDataRows)
        {
            var records = new List<string[]>();
            string[]? header = null;
            foreach (var record in ReadRecords(text, relativePathForError))
            {
                foreach (var field in record)
                {
                    if (field.Length > MaxFieldChars)
                    {
                        throw new ToolException(ErrorCodes.LimitExceeded,
                            "字段超过 64 KiB 限制：" + relativePathForError);
                    }
                }
                if (header == null)
                {
                    header = record;
                    continue;
                }
                if (record.Length != header.Length)
                {
                    throw new ToolException(ErrorCodes.CsvInvalid,
                        $"列数不一致：{relativePathForError} 第 {records.Count + 1} 条数据记录" +
                        $"（期望 {header.Length} 列，实际 {record.Length} 列）。");
                }
                records.Add(record);
                if (records.Count > maxDataRows)
                {
                    throw new ToolException(ErrorCodes.LimitExceeded,
                        "超过最大数据行数限制：" + relativePathForError);
                }
            }
            if (header == null)
            {
                throw new ToolException(ErrorCodes.CsvInvalid,
                    "文件为空或没有表头：" + relativePathForError);
            }
            return new CsvTable(header, records, encodingName, fileBytes);
        }

        private static IEnumerable<string[]> ReadRecords(string text, string relativePathForError)
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            var index = 0;
            var inQuotes = false;
            var fieldStarted = false;

            while (index <= text.Length)
            {
                if (index == text.Length)
                {
                    if (fieldStarted || fields.Count > 0) fields.Add(field.ToString());
                    if (fields.Count > 0) yield return FlushRecord(fields);
                    yield break;
                }
                var ch = text[index];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (index + 1 < text.Length && text[index + 1] == '"')
                        {
                            field.Append('"');
                            index += 2;
                            continue;
                        }
                        inQuotes = false;
                        index++;
                        continue;
                    }
                    field.Append(ch);
                    index++;
                    continue;
                }
                if (ch == '"' && field.Length == 0 && !fieldStarted)
                {
                    inQuotes = true;
                    fieldStarted = true;
                    index++;
                    continue;
                }
                if (ch == ',')
                {
                    fields.Add(field.ToString());
                    field.Length = 0;
                    fieldStarted = false;
                    index++;
                    continue;
                }
                if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                    index++;
                    fields.Add(field.ToString());
                    var record = FlushRecord(fields);
                    // 跳过完全空白的行（如文件末尾空行）。
                    if (!(record.Length == 1 && record[0].Length == 0)) yield return record;
                    field.Length = 0;
                    fieldStarted = false;
                    continue;
                }
                field.Append(ch);
                fieldStarted = true;
                index++;
            }
        }

        private static string[] FlushRecord(List<string> fields)
        {
            var record = fields.ToArray();
            fields.Clear();
            return record;
        }
    }
}
