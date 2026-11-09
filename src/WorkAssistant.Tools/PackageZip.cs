using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>
    /// OOXML 包（ZIP）读写基础设施：
    /// - 纯托管 System.IO.Compression，无 native 依赖（正式版 §11 文档引擎要求：
    ///   无 Win8/10 专属 API、native 库清单为空，规避 Win7 加载风险）。
    /// - 写入为确定性 ZIP（固定时间戳），保证同输入同输出（SHA-256 可复现）。
    /// </summary>
    public static class PackageZip
    {
        public static void ValidateArchive(ZipArchive archive)
        {
            if (archive.Entries.Count > DocumentLimits.MaxEntries) throw new ToolException(ErrorCodes.LimitExceeded, "Office 部件数量超限。 ");
            long total = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                DocumentLimits.Check();
                if (!names.Add(entry.FullName)) throw new ToolException(ErrorCodes.UnsupportedFormat, "Office 包含重复部件。 ");
                if (entry.FullName.Contains("..") || entry.FullName.StartsWith("/", StringComparison.Ordinal) || entry.FullName.Contains("\\"))
                    throw new ToolException(ErrorCodes.UnsupportedFormat, "Office 部件路径非法。 ");
                total = checked(total + entry.Length);
                if (total > DocumentLimits.MaxExpandedBytes || entry.Length > DocumentLimits.MaxExpandedBytes ||
                    (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 200))
                    throw new ToolException(ErrorCodes.LimitExceeded, "Office 解压大小或压缩比超限。 ");
            }
        }
        public static Stream OpenEntry(ZipArchiveEntry entry)
        {
            if (entry.Length > DocumentLimits.MaxExpandedBytes) throw new ToolException(ErrorCodes.LimitExceeded, "Office 部件过大。 ");
            using var stream = entry.Open();
            return new MemoryStream(DocumentLimits.ReadBounded(stream), false);
        }
        public static void ValidatePassiveTemplate(string filePath)
        {
            using var archive = ZipFile.OpenRead(filePath); ValidateArchive(archive);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName.ToLowerInvariant();
                if (name.Contains("vbaproject") || name.Contains("/embeddings/") || name.Contains("activex"))
                    throw new ToolException(ErrorCodes.UnsupportedFormat, "不接受带宏或嵌入对象的模板。 ");
                if (name.EndsWith(".rels", StringComparison.Ordinal))
                {
                    using var stream = OpenEntry(entry); using var reader = DocumentLimits.Xml(stream);
                    while (reader.Read()) if (reader.NodeType == System.Xml.XmlNodeType.Element &&
                        string.Equals(reader.GetAttribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                        throw new ToolException(ErrorCodes.UnsupportedFormat, "模板含外部关系，请移除外部引用后重试。 ");
                }
            }
        }
        /// <summary>固定条目时间戳（1980-01-01 为 ZIP 规范最早可用值）。</summary>
        private static readonly DateTime FixedTimestamp = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>把 (路径, 内容) 列表写成 ZIP 文件（先写临时文件再提交由调用方负责）。</summary>
        public static byte[] WriteDeterministic(IList<KeyValuePair<string, byte[]>> entries)
        {
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var entry in entries)
                {
                    var zipEntry = archive.CreateEntry(entry.Key, CompressionLevel.Optimal);
                    zipEntry.LastWriteTime = FixedTimestamp;
                    using var stream = zipEntry.Open();
                    stream.Write(entry.Value, 0, entry.Value.Length);
                }
            }
            return output.ToArray();
        }

        /// <summary>读取 ZIP 中指定条目；不存在返回 null。</summary>
        public static byte[]? ReadEntry(string filePath, string entryName)
        {
            using var archive = ZipFile.OpenRead(filePath);
            ValidateArchive(archive);
            var entry = archive.GetEntry(entryName);
            if (entry == null) return null;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            var bytes = DocumentLimits.ReadBounded(stream);
            buffer.Write(bytes, 0, bytes.Length);
            return buffer.ToArray();
        }

        /// <summary>读取 ZIP 全部条目名。</summary>
        public static List<string> ListEntries(string filePath)
        {
            var result = new List<string>();
            using (var archive = ZipFile.OpenRead(filePath))
            {
                foreach (var entry in archive.Entries)
                {
                    result.Add(entry.FullName);
                }
            }
            return result;
        }

        /// <summary>UTF-8（无 BOM）便捷方法。</summary>
        public static byte[] Utf8(string text) => new UTF8Encoding(false).GetBytes(text);

        /// <summary>读取条目为 UTF-8 文本。</summary>
        public static string ReadUtf8(byte[] bytes) => new UTF8Encoding(false).GetString(bytes);

        /// <summary>校验文件是可打开的 ZIP 包（OOXML 基础检查）。</summary>
        public static void AssertZipOpenable(string fullPath, string relativePathForError)
        {
            try
            {
                using var archive = ZipFile.OpenRead(fullPath);
                ValidateArchive(archive);
                if (archive.Entries.Count == 0)
                {
                    throw new ToolException(ErrorCodes.UnsupportedFormat,
                        "压缩包为空：" + relativePathForError);
                }
            }
            catch (InvalidDataException)
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat,
                    "不是有效的 Office/PDF 兼容压缩包（文件可能已损坏）：" + relativePathForError);
            }
            catch (IOException ex)
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat,
                    "读取文件失败：" + relativePathForError + "；" + ex.Message);
            }
        }
    }
}
