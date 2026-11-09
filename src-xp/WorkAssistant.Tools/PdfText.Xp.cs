// XP 变体（替换 src/WorkAssistant.Tools/PdfText.cs）：
// PdfPig 0.1.9 无 net40 资产。恢复仓库自研纯托管解析器（2026-11-01 版本）：
// FlateDecode（DeflateStream，net40 自带）、字面/十六进制字符串、Tj/TJ/'/" 操作符、
// ToUnicode CMap（含 CJK，全文档合并为已知限制）。超限不静默截断（与主线一致）。

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public sealed class PdfPageText
    {
        public int PageNumber { get; }
        public string Text { get; }
        public bool Truncated { get; }
        // XP 线无本地 OCR（SkiaSharp/Tesseract 无 net40 资产），OCR 成员保持兼容默认值。
        public bool OcrUsed => false;
        public double? OcrConfidence => null;
        public bool OcrImageFallback => false;

        public PdfPageText(int pageNumber, string text, bool truncated)
        {
            PageNumber = pageNumber;
            Text = text;
            Truncated = truncated;
        }
    }

    public sealed class PdfTextResult
    {
        public int PageCount { get; }
        public IList<PdfPageText> Pages { get; }
        public int TotalChars { get; }
        /// <summary>有页面资源但没有可提取文字 → 可能是扫描件（明确提示，不与空文档混淆）。</summary>
        public bool LikelyScanned { get; }
        public int OcrPages => 0;
        public int[] LowConfidencePages => new int[0];
        public string OcrNotice => "正文来自 PDF 原有文字层。";

        public PdfTextResult(int pageCount, IList<PdfPageText> pages,
            int totalChars, bool likelyScanned)
        {
            PageCount = pageCount;
            Pages = pages;
            TotalChars = totalChars;
            LikelyScanned = likelyScanned;
        }
    }

    /// <summary>
    /// 最小 PDF 文本提取器（XP 变体：PdfPig 无 net40 资产，恢复仓库自研解析器。
    /// 正式版 §8.3：文本型 PDF、保留页码、区分扫描件与空文档）：
    /// - 支持 FlateDecode 内容流、Tj/TJ/' 操作符、字面与十六进制字符串、ToUnicode CMap（含 CJK）。
    /// - 不做 OCR；LZW/DCT 等未支持的过滤器跳过对应流并在结果中提示。
    /// - 纯托管实现（latin1 1:1 映射扫描对象），无 native 依赖。
    /// </summary>
    public static partial class PdfText
    {
        public const long MaxFileBytes = 10L * 1024 * 1024;
        public const int MaxCharsPerPage = 20_000;
        public const int MaxTotalChars = 200_000;
        private const int DefaultMaxPages = 200;

        /// <summary>1 字节 = 1 字符的编码（二进制安全扫描用）。</summary>
        private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);
        /// <summary>S03：显式空映射（配合单字节回退），用于禁用全局 CMap 污染。</summary>
        private static readonly Dictionary<int, string> emptyCmap = new Dictionary<int, string>();

        public static PdfTextResult Extract(string fullPath, string relativePathForError,
            int maxPages = DefaultMaxPages)
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
                    "PDF 超过 10 MiB 限制：" + relativePathForError);
            }
            var raw = File.ReadAllBytes(fullPath);
            if (raw.Length < 8 || !Latin1.GetString(raw, 0, 5).Contains("%PDF"))
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat,
                    "不是有效的 PDF 文件（缺少 %PDF 头）：" + relativePathForError);
            }

            var document = Latin1.GetString(raw);
            var objects = ReadActiveObjects(document);
            var byNumber = objects.ToDictionary(o => o.Number);
            var (streams, unsupportedFilters) = DecodeStreams(objects);

            // 全局合并 ToUnicode CMap：无专属映射字体的回退（1.0 已知限制）。
            var (globalCmap, globalKeyBytes) = BuildUnicodeMap(streams);

            // R07 修复：按页树（Catalog→Pages→Kids 递归）排序；无法解析页树时回退到文件出现顺序。
            var pageOrder = ParsePageTreeOrder(byNumber, objects);
            var sawImages = false;
            var pageEntries = new List<(List<int> Contents, string FontSources)>();
            foreach (var pageNumber in pageOrder)
            {
                var dict = byNumber[pageNumber].DictText;
                if (dict.Contains("/Image") || dict.Contains("/DCTDecode")) sawImages = true;
                // 字体资源来源 = 本页字典（含继承拼接），供按字体解码。
                pageEntries.Add((ContentsRefs(dict), dict));
            }
            if (pageEntries.Count == 0 && streams.Count > 0)
            {
                // 无法解析页树：退化为单页、按对象号顺序拼接（仍可给出文字）。
                pageEntries.Add((streams.Keys.OrderBy(k => k).ToList(), ""));
            }
            if (maxPages <= 0) maxPages = DefaultMaxPages;

            var pageTexts = new List<PdfPageText>();
            var totalChars = 0;
            foreach (var (pageIndex, entry) in pageEntries.Select((p, i) => (i, p)))
            {
                if (pageIndex >= maxPages) break;
                DocumentLimits.Check();
                // 每页字体表：/Fname → (cmap, keyBytes)；无专属映射回退全局合并表。
                var pageFonts = PageFontMaps(entry.FontSources, byNumber, streams, out var anyFontMapped);
                // 页面资源解析出 per-font 表时禁用全局回退（避免其它字体的 CMap 污染）。
                var effectiveGlobalCmap = anyFontMapped ? emptyCmap : globalCmap;
                var effectiveGlobalKeyBytes = anyFontMapped ? 0 : globalKeyBytes;
                var sb = new StringBuilder();
                foreach (var objectNumber in entry.Contents)
                {
                    if (!streams.TryGetValue(objectNumber, out var content)) continue;
                    sb.Append(ExtractFromContent(content, pageFonts, effectiveGlobalCmap, effectiveGlobalKeyBytes));
                }
                var text = sb.ToString();
                if (text.Length > MaxCharsPerPage)
                {
                    throw new ToolException(ErrorCodes.LimitExceeded,
                        "单页 PDF 文字超限，请拆分文件；不提供静默截断结果。 ");
                }
                totalChars += text.Length;
                if (totalChars > MaxTotalChars)
                {
                    throw new ToolException(ErrorCodes.LimitExceeded,
                        "PDF 文本总量超过 200000 字符限制；请拆分文件或缩小页数范围：" +
                        relativePathForError);
                }
                pageTexts.Add(new PdfPageText(pageIndex + 1, text, false));
            }

            var likelyScanned = totalChars == 0 &&
                (sawImages || unsupportedFilters) &&
                pageEntries.Count > 0;
            return new PdfTextResult(pageEntries.Count, pageTexts, totalChars, likelyScanned);
        }

        // ---- 对象扫描 -------------------------------------------------------

        private sealed class PdfObject
        {
            public int Number;
            public string DictText = "";
            public string? StreamLatin1; // 未解码的流内容（latin1）；无流为 null
        }

        private static readonly Regex ObjectRegex =
            new(@"(?<!\d)(\d+)\s+\d+\s+obj\b", RegexOptions.Compiled);

        private static List<PdfObject> ScanObjects(string document)
        {
            var result = new List<PdfObject>();
            foreach (Match match in ObjectRegex.Matches(document))
            {
                if (!int.TryParse(match.Groups[1].Value, out var number)) continue;
                var obj = new PdfObject { Number = number };
                var bodyStart = match.Index + match.Length;
                var endObj = document.IndexOf("endobj", bodyStart, StringComparison.Ordinal);
                if (endObj < 0) endObj = document.Length;
                var body = document.Substring(bodyStart, endObj - bodyStart);
                var streamKeyword = body.IndexOf("stream", StringComparison.Ordinal);
                if (streamKeyword >= 0)
                {
                    obj.DictText = body.Substring(0, streamKeyword);
                    var afterKeyword = streamKeyword + "stream".Length;
                    if (afterKeyword < body.Length && body[afterKeyword] == '\r') afterKeyword++;
                    if (afterKeyword < body.Length && body[afterKeyword] == '\n') afterKeyword++;
                    var endStream = body.LastIndexOf("endstream", StringComparison.Ordinal);
                    if (endStream < 0) endStream = body.Length;
                    // 只剥离 endstream 前的一个 EOL（压缩数据本身可能以 0x0D/0x0A 结尾）。
                    var length = Math.Max(0, endStream - afterKeyword);
                    if (length > 0 && body[afterKeyword + length - 1] == '\n') length--;
                    if (length > 0 && body[afterKeyword + length - 1] == '\r') length--;
                    obj.StreamLatin1 = body.Substring(afterKeyword, length);
                }
                else
                {
                    obj.DictText = body;
                }
                result.Add(obj);
            }
            return result;
        }

        /// <summary>解码全部 FlateDecode 流；返回 (对象号->latin1 文本, 是否存在不支持的过滤器)。</summary>
        private static (Dictionary<int, string> Streams, bool Unsupported) DecodeStreams(
            List<PdfObject> objects)
        {
            var streams = new Dictionary<int, string>();
            var unsupported = false;
            foreach (var obj in objects)
            {
                var dict = obj.DictText;
                // 过滤器检查先于流存在性判断：无流的对象（如图像 XObject 声明）也参与扫描件判定。
                if (dict.Contains("/LZWDecode") || dict.Contains("/DCTDecode") ||
                    dict.Contains("/JPXDecode") || dict.Contains("/CCITTFaxDecode"))
                {
                    unsupported = true;
                    continue;
                }
                if (obj.StreamLatin1 == null) continue;
                if (dict.Contains("/FlateDecode"))
                {
                    var bytes = Latin1.GetBytes(obj.StreamLatin1);
                    var inflated = TryInflate(bytes);
                    if (inflated != null)
                    {
                        streams[obj.Number] = Latin1.GetString(inflated);
                    }
                }
                else if (dict.Contains("/Length"))
                {
                    // 无压缩流。
                    streams[obj.Number] = obj.StreamLatin1;
                }
            }
            return (streams, unsupported);
        }

        private static byte[]? TryInflate(byte[] zlib)
        {
            // zlib 流带 2 字节头；部分生成器直接给 deflate。两种都尝试。
            for (var skip = 0; skip <= 2; skip += 2)
            {
                if (skip >= zlib.Length) break;
                try
                {
                    using var output = new MemoryStream();
                    using (var deflate = new DeflateStream(
                        new MemoryStream(zlib, skip, zlib.Length - skip), CompressionMode.Decompress))
                    {
                        var buffer = new byte[8192]; int read;
                        while ((read = deflate.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            DocumentLimits.Check();
                            if (output.Length + read > 32L * 1024 * 1024) throw new ToolException(ErrorCodes.LimitExceeded, "PDF 单个解压流超过 32 MiB 限制。");
                            output.Write(buffer, 0, read);
                        }
                    }
                    return output.ToArray();
                }
                catch (InvalidDataException)
                {
                }
                catch (IOException)
                {
                }
            }
            return null;
        }

        // ---- ToUnicode CMap --------------------------------------------------

        private static (Dictionary<int, string> Map, int KeyBytes) BuildUnicodeMap(
            Dictionary<int, string> streams)
        {
            var map = new Dictionary<int, string>();
            var keyBytes = 0;
            foreach (var content in streams.Values)
            {
                if (!content.Contains("beginbfchar") && !content.Contains("beginbfrange")) continue;
                keyBytes = Math.Max(keyBytes, ParseCMap(content, map));
            }
            return (map, keyBytes);
        }

        private static int ParseCMap(string content, Dictionary<int, string> map)
        {
            var keyBytes = 0;
            var bfchar = Regex.Matches(content,
                @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline);
            foreach (Match block in bfchar)
            {
                foreach (Match pair in Regex.Matches(block.Groups[1].Value,
                    @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
                {
                    var keyHex = pair.Groups[1].Value;
                    keyBytes = Math.Max(keyBytes, keyHex.Length / 2);
                    if (int.TryParse(keyHex, System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out var code))
                    {
                        map[code] = HexToUnicode(pair.Groups[2].Value);
                    }
                }
            }
            var bfrange = Regex.Matches(content,
                @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline);
            foreach (Match block in bfrange)
            {
                // 按行解析：数组形式 <lo> <hi> [<v>...] 与三元组形式 <lo> <hi> <start>
                // 不能混用同一个宽松正则（数组内部的 <v> <v> <v> 会被误判为三元组）。
                foreach (var rawLine in block.Groups[1].Value.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    var arrayForm = Regex.Match(line,
                        @"^<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*\[(.*)\]$");
                    if (arrayForm.Success)
                    {
                        var keyHex = arrayForm.Groups[1].Value;
                        keyBytes = Math.Max(keyBytes, keyHex.Length / 2);
                        var lo = Convert.ToInt32(keyHex, 16);
                        var values = Regex.Matches(arrayForm.Groups[3].Value, @"<([0-9A-Fa-f]+)>");
                        for (var i = 0; i < values.Count; i++)
                        {
                            map[lo + i] = HexToUnicode(values[i].Groups[1].Value);
                        }
                        continue;
                    }
                    var triple = Regex.Match(line,
                        @"^<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>$");
                    if (triple.Success)
                    {
                        var keyHex = triple.Groups[1].Value;
                        keyBytes = Math.Max(keyBytes, keyHex.Length / 2);
                        var lo = Convert.ToInt32(triple.Groups[1].Value, 16);
                        var hi = Convert.ToInt32(triple.Groups[2].Value, 16);
                        var start = Convert.ToInt32(triple.Groups[3].Value, 16);
                        for (var code = lo; code <= hi && code - lo < 65536; code++)
                        {
                            map[code] = char.ConvertFromUtf32(start + (code - lo));
                        }
                    }
                }
            }
            return keyBytes;
        }

        private static string HexToUnicode(string hex)
        {
            var sb = new StringBuilder(hex.Length / 4);
            for (var i = 0; i + 3 < hex.Length; i += 4)
            {
                sb.Append(char.ConvertFromUtf32(Convert.ToInt32(
                    hex.Substring(i, 4), 16)));
            }
            return sb.ToString();
        }

        // ---- 页与内容引用 ------------------------------------------------------

        private static bool IsPageDict(string dict)
        {
            var match = Regex.Match(dict, @"/Type\s*/Pages?\b");
            return match.Success && match.Value.TrimEnd().EndsWith("Page", StringComparison.Ordinal);
        }

        // ---- R07：页树顺序与按字体解码 -----------------------------------------

        /// <summary>Catalog→/Pages→/Kids 递归得到页对象顺序；带环与深度防护；失败返回空表由调用方回退。</summary>
        private static List<int> ParsePageTreeOrder(Dictionary<int, PdfObject> byNumber, List<PdfObject> objects)
        {
            int? FindRootPages()
            {
                foreach (var obj in objects)
                {
                    var dict = obj.DictText;
                    if (Regex.IsMatch(dict, @"/Type\s*/Catalog\b"))
                    {
                        var match = Regex.Match(dict, @"/Pages\s+(\d+)\s+\d+\s+R");
                        if (match.Success && int.TryParse(match.Groups[1].Value, out var root)) return root;
                    }
                }
                return null;
            }

            var pages = new List<int>();
            var rootPages = FindRootPages();
            if (rootPages == null) return pages;
            var visited = new HashSet<int>();
            var inherited = new Dictionary<int, string>();

            void Walk(int nodeNumber, string inheritedResources, int depth)
            {
                if (depth > 64 || pages.Count > 5000 || !visited.Add(nodeNumber)) return;
                if (!byNumber.TryGetValue(nodeNumber, out var node)) return;
                var dict = node.DictText;
                // 子节点继承本节点的 /Resources 声明（拼在子字典之后，子定义优先）。
                var resources = Regex.Match(dict, @"/Resources\b[\s\S]{0,4000}");
                var combined = resources.Success ? dict.Substring(resources.Index) + "\n" + inheritedResources : inheritedResources;
                if (Regex.IsMatch(dict, @"/Type\s*/Pages\b"))
                {
                    var kids = Regex.Match(dict, @"/Kids\s*\[([^\]]*)\]");
                    if (kids.Success)
                    {
                        foreach (Match kid in Regex.Matches(kids.Groups[1].Value, @"(\d+)\s+\d+\s+R"))
                        {
                            if (int.TryParse(kid.Groups[1].Value, out var child)) Walk(child, combined, depth + 1);
                        }
                    }
                    return;
                }
                if (Regex.IsMatch(dict, @"/Type\s*/Page\b"))
                {
                    pages.Add(nodeNumber);
                    inherited[nodeNumber] = combined;
                }
            }

            Walk(rootPages.Value, "", 0);
            // 把继承资源回填到字典文本（供 PageFontMaps 使用：本页声明在前、继承在后）。
            foreach (var number in pages)
            {
                if (inherited.TryGetValue(number, out var extra) && extra.Length > 0)
                    byNumber[number].DictText = byNumber[number].DictText + "\n" + extra;
            }
            return pages;
        }

        /// <summary>解析页面资源里的字体表：/Fname → (ToUnicode CMap, keyBytes)。引用式与内联式 /Resources 均支持。</summary>
        private static Dictionary<string, (Dictionary<int, string> Map, int KeyBytes)> PageFontMaps(
            string pageDictText, Dictionary<int, PdfObject> byNumber, Dictionary<int, string> streams,
            out bool anyFontMapped)
        {
            var result = new Dictionary<string, (Dictionary<int, string>, int)>();
            anyFontMapped = false;
            var resources = Regex.Match(pageDictText, @"/Resources\s*(\d+)\s+\d+\s+R");
            string resourcesText = pageDictText;
            if (resources.Success && int.TryParse(resources.Groups[1].Value, out var resourcesNumber)
                && byNumber.TryGetValue(resourcesNumber, out var resourcesObject))
            {
                resourcesText = resourcesObject.DictText;
            }
            // 兼容两种形态：/Font N 0 R（引用式）与 /Font << /F1 n 0 R ... >>（内联式）。
            // 内联式取 /Font 起始后的窗口扫描字体引用；引用式优先使用字体字典对象的全文。
            var fonts = Regex.Match(resourcesText, @"/Font\s*(\d+)\s+\d+\s+R");
            string fontText;
            if (fonts.Success && int.TryParse(fonts.Groups[1].Value, out var fontObjectNumber)
                && byNumber.TryGetValue(fontObjectNumber, out var fontDictObject))
            {
                fontText = fontDictObject.DictText;
            }
            else
            {
                var inline = Regex.Match(resourcesText, @"/Font\s*<<");
                if (!inline.Success) return result;
                var window = resourcesText.Substring(inline.Index);
                fontText = window.Length > 4096 ? window.Substring(0, 4096) : window;
            }
            foreach (Match entry in Regex.Matches(fontText, @"/(\w+)\s+(\d+)\s+\d+\s+R"))
            {
                var fontName = entry.Groups[1].Value;
                if (!int.TryParse(entry.Groups[2].Value, out var fontReference)) continue;
                if (!byNumber.TryGetValue(fontReference, out var fontObject)) continue;
                var toUnicode = Regex.Match(fontObject.DictText, @"/ToUnicode\s+(\d+)\s+\d+\s+R");
                // S03：无 ToUnicode 的字体登记为显式空映射（标准单字节编码回退），
                // 切换到该字体时不得沿用上一字体或其它字体的 CMap。
                if (!toUnicode.Success || !int.TryParse(toUnicode.Groups[1].Value, out var mapStream) ||
                    !streams.TryGetValue(mapStream, out var cmapContent) ||
                    !cmapContent.Contains("beginbfchar") && !cmapContent.Contains("beginbfrange"))
                {
                    result[fontName] = (new Dictionary<int, string>(), 0);
                    continue;
                }
                var map = new Dictionary<int, string>();
                var keyBytes = ParseCMap(cmapContent, map);
                if (map.Count > 0)
                {
                    result[fontName] = (map, keyBytes);
                    anyFontMapped = true;
                }
                else
                {
                    result[fontName] = (new Dictionary<int, string>(), 0);
                }
            }
            return result;
        }

        private static List<int> ContentsRefs(string dict)
        {
            var result = new List<int>();
            var array = Regex.Match(dict, @"/Contents\s*\[([^\]]*)\]");
            if (array.Success)
            {
                foreach (Match reference in Regex.Matches(array.Groups[1].Value, @"(\d+)\s+\d+\s+R"))
                {
                    result.Add(int.Parse(reference.Groups[1].Value));
                }
                return result;
            }
            var single = Regex.Match(dict, @"/Contents\s+(\d+)\s+\d+\s+R");
            if (single.Success)
            {
                result.Add(int.Parse(single.Groups[1].Value));
            }
            return result;
        }

        // ---- 内容流文本操作符解析 ----------------------------------------------

        private static string ExtractFromContent(string content,
            Dictionary<string, (Dictionary<int, string> Map, int KeyBytes)> pageFonts,
            Dictionary<int, string> globalCmap, int globalKeyBytes)
        {
            var sb = new StringBuilder();
            var index = 0;
            var length = content.Length;
            var pendingText = "";
            var arrayStrings = new List<string>();
            var inArray = false;
            // R07：按字体解码 —— /Fname size Tf 切换当前字体的 ToUnicode 映射。
            Dictionary<int, string> cmap = globalCmap;
            int cmapKeyBytes = globalKeyBytes;
            var pendingFontName = "";

            string Decode(List<byte> bytes)
            {
                var result = new StringBuilder();
                if (cmapKeyBytes >= 2 && cmap.Count > 0)
                {
                    // 2 字节 CID 字体（常见于 CJK 文本 PDF）。
                    // 未命中映射的字节对按单字节回退（同文档混合西文字体时保留可读文本）。
                    for (var i = 0; i + 1 < bytes.Count; i += 2)
                    {
                        var code = bytes[i] << 8 | bytes[i + 1];
                        if (cmap.TryGetValue(code, out var mapped))
                        {
                            result.Append(mapped);
                            continue;
                        }
                        AppendByte(result, bytes[i]);
                        AppendByte(result, bytes[i + 1]);
                    }
                    if (bytes.Count % 2 == 1)
                    {
                        AppendByte(result, bytes[bytes.Count - 1]);
                    }
                    return result.ToString();
                }
                for (var i = 0; i < bytes.Count; i++)
                {
                    var code = bytes[i];
                    if (cmap.TryGetValue(code, out var mapped))
                    {
                        result.Append(mapped);
                    }
                    else
                    {
                        AppendByte(result, code);
                    }
                }
                return result.ToString();
            }

            void AppendText(string text)
            {
                if (text.Length == 0) return;
                sb.Append(text);
            }

            void Newline()
            {
                if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
            }

            while (index < length)
            {
                var c = content[index];
                if (char.IsWhiteSpace(c))
                {
                    index++;
                    continue;
                }

                // 字面字符串 (…)
                if (c == '(')
                {
                    var bytes = ParseLiteral(content, ref index);
                    var decoded = Decode(bytes);
                    if (inArray) arrayStrings.Add(decoded);
                    else pendingText = decoded;
                    continue;
                }

                // 十六进制字符串 <…>（字典 <<…>> 中的非文本值由操作符分支忽略）。
                if (c == '<' && index + 1 < length && content[index + 1] != '<')
                {
                    var end = content.IndexOf('>', index);
                    if (end < 0) break;
                    var hex = new StringBuilder();
                    for (var i = index + 1; i < end; i++)
                    {
                        if (!char.IsWhiteSpace(content[i])) hex.Append(content[i]);
                    }
                    var bytes = new List<byte>();
                    for (var i = 0; i + 1 < hex.Length; i += 2)
                    {
                        bytes.Add(Convert.ToByte(hex.ToString(i, 2), 16));
                    }
                    var decoded = Decode(bytes);
                    if (inArray) arrayStrings.Add(decoded);
                    else pendingText = decoded;
                    index = end + 1;
                    continue;
                }

                if (c == '[')
                {
                    inArray = true;
                    arrayStrings = new List<string>();
                    index++;
                    continue;
                }
                if (c == ']')
                {
                    inArray = false;
                    index++;
                    continue;
                }

                // '（换行显示）与 "（设置字距后换行显示）操作符。
                if (c == '\'' || c == '"')
                {
                    Newline();
                    AppendText(pendingText);
                    pendingText = "";
                    index++;
                    continue;
                }

                if (c == '/')
                {
                    var start = index + 1;
                    var cursor = start;
                    while (cursor < length && !char.IsWhiteSpace(content[cursor]) && content[cursor] != '(' &&
                           content[cursor] != '<' && content[cursor] != '[' && content[cursor] != '/' &&
                           content[cursor] != ']' && content[cursor] != ')')
                        cursor++;
                    pendingFontName = content.Substring(start, cursor - start);
                    index = cursor;
                    continue;
                }

                if (char.IsLetter(c))
                {
                    var start = index;
                    while (index < length && char.IsLetter(content[index])) index++;
                    var op = content.Substring(start, index - start);
                    if (op == "Tf" && pendingFontName.Length > 0)
                    {
                        if (pageFonts.TryGetValue(pendingFontName, out var font))
                        {
                            cmap = font.Map;
                            cmapKeyBytes = font.KeyBytes;
                        }
                        else
                        {
                            cmap = globalCmap;
                            cmapKeyBytes = globalKeyBytes;
                        }
                    }
                    pendingFontName = "";
                    switch (op)
                    {
                        case "Tj":
                            AppendText(pendingText);
                            pendingText = "";
                            break;
                        case "TJ":
                            foreach (var s in arrayStrings) AppendText(s);
                            arrayStrings = new List<string>();
                            pendingText = "";
                            break;
                        case "T*":
                        case "Td":
                        case "TD":
                        case "BT":
                        case "ET":
                            Newline();
                            pendingText = "";
                            break;
                        default:
                            // 其它操作符（Tf/cm/Tm 等）：参数作废。
                            pendingText = "";
                            break;
                    }
                    continue;
                }
                index++;
            }
            return sb.ToString();
        }

        /// <summary>单字节回退：可打印 ASCII/latin1 保留，控制字符换为空格。</summary>
        private static void AppendByte(StringBuilder result, byte code)
        {
            result.Append(code >= 32 && code < 127 ? (char)code : ' ');
        }

        /// <summary>解析 (…) 字面字符串：处理 \( \) \\ \n \r \t \b \f 与八进制转义。</summary>
        private static List<byte> ParseLiteral(string content, ref int index)
        {            var bytes = new List<byte>();
            index++; // 跳过 '('
            var depth = 1;
            while (index < content.Length)
            {
                var c = content[index];
                if (c == '\\')
                {
                    index++;
                    if (index >= content.Length) break;
                    var escaped = content[index];
                    switch (escaped)
                    {
                        case 'n': bytes.Add((byte)'\n'); break;
                        case 'r': bytes.Add((byte)'\r'); break;
                        case 't': bytes.Add((byte)'\t'); break;
                        case 'b': bytes.Add(8); break;
                        case 'f': bytes.Add(12); break;
                        case '(': bytes.Add((byte)'('); break;
                        case ')': bytes.Add((byte)')'); break;
                        case '\\': bytes.Add((byte)'\\'); break;
                        default:
                            if (escaped >= '0' && escaped <= '7')
                            {
                                var octal = new string(escaped, 1);
                                index++;
                                while (index < content.Length && octal.Length < 3 &&
                                       content[index] >= '0' && content[index] <= '7')
                                {
                                    octal += content[index];
                                    index++;
                                }
                                index--; // 主循环会再前进
                                bytes.Add((byte)Convert.ToInt32(octal, 8));
                            }
                            else
                            {
                                bytes.Add((byte)escaped);
                            }
                            break;
                    }
                    index++;
                    continue;
                }
                if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        index++;
                        break;
                    }
                }
                else if (c == '(')
                {
                    depth++;
                }
                bytes.Add((byte)c);
                index++;
            }
            return bytes;
        }
    }
}
