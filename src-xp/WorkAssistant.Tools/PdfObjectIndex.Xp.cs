using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public static partial class PdfText
    {
        // ISO 32000-1 §7.5: newest xref section wins, including free entries.
        // All downstream consumers receive this same active object set.
        private sealed class XrefEntry
        {
            internal int Kind, Offset, Generation;
        }
        private sealed class PdfRef
        {
            internal int Number, Generation;
        }
        private sealed class PdfSyntax
        {
            internal readonly string Text;
            internal int Position;
            internal PdfSyntax(string text, int position = 0) { Text = text; Position = position; }
            internal void Space()
            {
                while (Position < Text.Length)
                {
                    if (char.IsWhiteSpace(Text[Position]) || Text[Position] == '\0') { Position++; continue; }
                    if (Text[Position] != '%') break;
                    while (Position < Text.Length && Text[Position] != '\r' && Text[Position] != '\n') Position++;
                }
            }
            internal string Token()
            {
                Space(); var start = Position;
                while (Position < Text.Length && !char.IsWhiteSpace(Text[Position]) && "()<>[]{}/%\0".IndexOf(Text[Position]) < 0) Position++;
                if (Position == start) throw InvalidPdf("无法读取 PDF 标记。");
                return Text.Substring(start, Position - start);
            }
            internal object Value(int depth = 0)
            {
                if (depth > 64) throw InvalidPdf("PDF 对象嵌套过深。");
                Space(); if (Position >= Text.Length) throw InvalidPdf("PDF 对象意外结束。");
                if (Text[Position] == '<' && Position + 1 < Text.Length && Text[Position + 1] == '<')
                {
                    Position += 2; var dict = new Dictionary<string, object>();
                    while (true)
                    {
                        Space(); if (Position + 1 < Text.Length && Text.Substring(Position, 2) == ">>") { Position += 2; return dict; }
                        if (Position >= Text.Length || Text[Position++] != '/') throw InvalidPdf("PDF 字典键无效。");
                        var key = Token(); dict[key] = Value(depth + 1);
                    }
                }
                if (Text[Position] == '[')
                {
                    Position++; var list = new List<object>();
                    while (true) { Space(); if (Position >= Text.Length) throw InvalidPdf("PDF 数组意外结束。"); if (Text[Position] == ']') { Position++; return list; } list.Add(Value(depth + 1)); }
                }
                if (Text[Position] == '(')
                {
                    var start = Position++; var nesting = 1;
                    while (Position < Text.Length && nesting > 0)
                    {
                        var c = Text[Position++];
                        if (c == '\\') { if (Position < Text.Length) Position++; }
                        else if (c == '(') nesting++;
                        else if (c == ')') nesting--;
                    }
                    if (nesting != 0) throw InvalidPdf("PDF 字符串意外结束。");
                    return Text.Substring(start, Position - start);
                }
                if (Text[Position] == '<')
                {
                    var end = Text.IndexOf('>', Position + 1); if (end < 0) throw InvalidPdf("PDF 十六进制字符串无效。");
                    var raw = Text.Substring(Position, end + 1 - Position); Position = end + 1; return raw;
                }
                if (Text[Position] == '/') { Position++; return "/" + Token(); }
                var token = Token();
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return token;
                var saved = Position;
                try
                {
                    var second = Token();
                    if (int.TryParse(second, out var generation) && Token() == "R") return new PdfRef { Number = number, Generation = generation };
                }
                catch (ToolException) { }
                Position = saved; return number;
            }
        }

        private static ToolException InvalidPdf(string detail) => new ToolException(ErrorCodes.UnsupportedFormat, detail);
        private static int DictInt(Dictionary<string, object> dict, string key, int fallback = -1) => dict.TryGetValue(key, out var v) && v is int n ? n : fallback;
        private static int[] DictArray(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var v) || !(v is List<object> list) || list.Any(x => !(x is int))) throw InvalidPdf("PDF /" + key + " 数组无效。");
            return list.Cast<int>().ToArray();
        }

        private static List<PdfObject> ReadActiveObjects(string document)
        {
            var starts = Regex.Matches(document, @"startxref\s+(\d+)\s+%%EOF");
            if (starts.Count == 0)
            {
                // Retain support for legacy generated, xref-less fixtures only when unambiguous.
                var recovered = ScanObjects(document);
                if (recovered.GroupBy(o => o.Number).Any(g => g.Count() > 1)) throw InvalidPdf("PDF 含多个对象版本但缺少有效交叉引用表。");
                return recovered;
            }
            var entries = new Dictionary<int, XrefEntry>(); var visited = new HashSet<int>(); PdfRef? root = null;
            var offset = int.Parse(starts[starts.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture);
            void Section(int at, bool supplement = false)
            {
                DocumentLimits.Check();
                if (at < 0 || at >= document.Length || !visited.Add(at) || visited.Count > 1024) throw InvalidPdf("PDF 交叉引用链无效或循环。");
                var syntax = new PdfSyntax(document, at); syntax.Space();
                Dictionary<string, object> trailer;
                var current = new Dictionary<int, XrefEntry>();
                if (document.Substring(syntax.Position).StartsWith("xref", StringComparison.Ordinal))
                {
                    syntax.Position += 4;
                    while (true)
                    {
                        var first = syntax.Token(); if (first == "trailer") break;
                        if (!int.TryParse(first, out var begin) || !int.TryParse(syntax.Token(), out var count) || begin < 0 || count < 0 || (long)begin + count > 1000000) throw InvalidPdf("PDF xref 范围无效。");
                        for (var i = 0; i < count; i++)
                        {
                            if (!int.TryParse(syntax.Token(), out var position) || !int.TryParse(syntax.Token(), out var gen)) throw InvalidPdf("PDF xref 条目无效。");
                            var kind = syntax.Token(); if (kind != "n" && kind != "f") throw InvalidPdf("PDF xref 状态无效。");
                            current[begin + i] = new XrefEntry { Kind = kind == "n" ? 1 : 0, Offset = position, Generation = gen };
                        }
                    }
                    trailer = syntax.Value() as Dictionary<string, object> ?? throw InvalidPdf("PDF trailer 无效。");
                }
                else
                {
                    var obj = ReadIndexedObject(document, at, null, out trailer, out _);
                    if (!trailer.TryGetValue("Type", out var type) || !Equals(type, "/XRef")) throw InvalidPdf("startxref 未指向交叉引用表。");
                    var widths = DictArray(trailer, "W");
                    if (widths.Length != 3 || widths.Any(w => w < 0 || w > 4) || widths.Sum() == 0) throw InvalidPdf("PDF xref 字段宽度无效。");
                    var size = DictInt(trailer, "Size"); if (size < 0 || size > 1000000) throw InvalidPdf("PDF xref 大小无效。");
                    var index = trailer.ContainsKey("Index") ? DictArray(trailer, "Index") : new[] { 0, size };
                    if (index.Length % 2 != 0) throw InvalidPdf("PDF xref 索引无效。");
                    var bytes = DecodeIndexedStream(obj, trailer); var cursor = 0;
                    for (var k = 0; k < index.Length; k += 2)
                    {
                        if (index[k] < 0 || index[k + 1] < 0 || (long)index[k] + index[k + 1] > size) throw InvalidPdf("PDF xref 索引越界。");
                        for (var i = 0; i < index[k + 1]; i++)
                        {
                            var fields = new int[3];
                            for (var f = 0; f < 3; f++)
                            {
                                long value = f == 0 && widths[f] == 0 ? 1 : 0;
                                for (var b = 0; b < widths[f]; b++) { if (cursor >= bytes.Length) throw InvalidPdf("PDF xref 流不完整。"); value = (value << 8) | bytes[cursor++]; }
                                if (value > int.MaxValue) throw InvalidPdf("PDF xref 数值超限。"); fields[f] = (int)value;
                            }
                            if (fields[0] < 0 || fields[0] > 2) throw InvalidPdf("PDF xref 条目类型不受支持。");
                            current[index[k] + i] = new XrefEntry { Kind = fields[0], Offset = fields[1], Generation = fields[2] };
                        }
                    }
                }
                if (trailer.ContainsKey("Encrypt")) throw InvalidPdf("本版本不支持加密 PDF，请提供未加密副本。");
                if (root == null && trailer.TryGetValue("Root", out var catalog)) root = catalog as PdfRef;
                // In a hybrid section the supplemental stream overrides that section's table.
                var hybrid = DictInt(trailer, "XRefStm");
                if (hybrid >= 0) Section(hybrid, true);
                foreach (var pair in current) if (!entries.ContainsKey(pair.Key)) entries.Add(pair.Key, pair.Value);
                var previous = DictInt(trailer, "Prev"); if (!supplement && previous >= 0) Section(previous);
            }
            Section(offset);
            var active = new Dictionary<int, PdfObject>(); var loading = new HashSet<int>();
            PdfObject Load(int number)
            {
                if (active.TryGetValue(number, out var cached)) return cached;
                if (!entries.TryGetValue(number, out var entry) || entry.Kind == 0 || !loading.Add(number)) throw InvalidPdf("PDF 引用不存在、已释放或循环。");
                PdfObject obj;
                if (entry.Kind == 1)
                {
                    obj = ReadIndexedObject(document, entry.Offset, reference =>
                    {
                        if (!entries.TryGetValue(reference.Number, out var lengthEntry) || lengthEntry.Kind == 0 || (lengthEntry.Kind == 1 ? lengthEntry.Generation : 0) != reference.Generation) throw InvalidPdf("PDF 流长度引用代数不匹配。");
                        var scalar = Load(reference.Number).DictText.Trim(); return int.Parse(scalar, CultureInfo.InvariantCulture);
                    }, out _, out var gen);
                    if (obj.Number != number || gen != entry.Generation) throw InvalidPdf("PDF 对象号或代数与交叉引用不一致。");
                }
                else
                {
                    var container = Load(entry.Offset);
                    var parser = new PdfSyntax(container.DictText); var dict = parser.Value() as Dictionary<string, object> ?? throw InvalidPdf("PDF 对象流字典无效。");
                    if (!dict.TryGetValue("Type", out var t) || !Equals(t, "/ObjStm")) throw InvalidPdf("PDF 压缩对象容器类型无效。");
                    var count = DictInt(dict, "N"); var first = DictInt(dict, "First"); var bytes = DecodeIndexedStream(container, dict); var content = Latin1.GetString(bytes);
                    if (count < 0 || count > 100000 || first < 0 || first > content.Length || entry.Generation < 0 || entry.Generation >= count) throw InvalidPdf("PDF 对象流索引无效。");
                    var header = new PdfSyntax(content); var numbers = new int[count]; var positions = new int[count];
                    for (var i = 0; i < count; i++) { numbers[i] = int.Parse(header.Token(), CultureInfo.InvariantCulture); positions[i] = int.Parse(header.Token(), CultureInfo.InvariantCulture); }
                    var ix = entry.Generation; var start = (long)first + positions[ix]; var end = ix + 1 < count ? (long)first + positions[ix + 1] : content.Length;
                    if (numbers[ix] != number || header.Position > first || start < first || end < start || end > content.Length) throw InvalidPdf("PDF 压缩对象位置无效。");
                    obj = new PdfObject { Number = number, DictText = content.Substring((int)start, (int)(end - start)) };
                }
                loading.Remove(number); active[number] = obj; return obj;
            }
            foreach (var pair in entries) { DocumentLimits.Check(); if (pair.Value.Kind != 0 && pair.Key != 0) Load(pair.Key); }
            // Reject references to a freed object or an obsolete generation rather than read stale content.
            void ValidateReferences(object value)
            {
                if (value is PdfRef reference)
                {
                    var n = reference.Number; var gen = reference.Generation;
                    if (!entries.TryGetValue(n, out var e) || e.Kind == 0 || (e.Kind == 1 ? e.Generation : 0) != gen) throw InvalidPdf("PDF 含已释放或代数不匹配的对象引用。");
                }
                else if (value is Dictionary<string, object> d) foreach (var v in d.Values) ValidateReferences(v);
                else if (value is List<object> a) foreach (var v in a) ValidateReferences(v);
            }
            foreach (var obj in active.Values) ValidateReferences(new PdfSyntax(obj.DictText).Value());
            if (root != null && (!entries.TryGetValue(root.Number, out var re) || re.Kind == 0 || (re.Kind == 1 ? re.Generation : 0) != root.Generation)) throw InvalidPdf("PDF 根对象引用无效。");
            return active.Values.OrderBy(o => root != null && o.Number == root.Number ? 0 : 1).ThenBy(o => o.Number).ToList();
        }

        private static PdfObject ReadIndexedObject(string document, int offset, Func<PdfRef, int>? lengthResolver, out Dictionary<string, object> dict, out int generation)
        {
            if (offset < 0 || offset >= document.Length) throw InvalidPdf("PDF 对象偏移越界。");
            var syntax = new PdfSyntax(document, offset); var number = int.Parse(syntax.Token(), CultureInfo.InvariantCulture); generation = int.Parse(syntax.Token(), CultureInfo.InvariantCulture);
            if (syntax.Token() != "obj") throw InvalidPdf("PDF 对象头无效。");
            syntax.Space(); var begin = syntax.Position; var value = syntax.Value(); var end = syntax.Position;
            dict = value as Dictionary<string, object> ?? new Dictionary<string, object>();
            var obj = new PdfObject { Number = number, DictText = document.Substring(begin, end - begin) };
            syntax.Space();
            if (syntax.Position + 6 <= document.Length && document.Substring(syntax.Position, 6) == "stream")
            {
                syntax.Position += 6;
                if (syntax.Position < document.Length && document[syntax.Position] == '\r')
                { syntax.Position++; if (syntax.Position < document.Length && document[syntax.Position] == '\n') syntax.Position++; }
                else if (syntax.Position < document.Length && document[syntax.Position] == '\n') syntax.Position++;
                else throw InvalidPdf("PDF 流起始换行无效。");
                var length = DictInt(dict, "Length");
                if (length < 0 && dict.TryGetValue("Length", out var l) && l is PdfRef r && lengthResolver != null) length = lengthResolver(r);
                if (length < 0 || (long)syntax.Position + length > document.Length) throw InvalidPdf("PDF 流长度无效。");
                obj.StreamLatin1 = document.Substring(syntax.Position, length); syntax.Position += length; syntax.Space();
                if (syntax.Token() != "endstream") throw InvalidPdf("PDF 流长度与结束标记不一致。");
            }
            if (syntax.Token() != "endobj") throw InvalidPdf("PDF 对象缺少结束标记。");
            return obj;
        }

        private static byte[] DecodeIndexedStream(PdfObject obj, Dictionary<string, object> dict)
        {
            if (obj.StreamLatin1 == null) throw InvalidPdf("PDF 索引流缺失。");
            var bytes = Latin1.GetBytes(obj.StreamLatin1);
            if (dict.TryGetValue("Filter", out var filter))
            {
                if (!(Equals(filter, "/FlateDecode") || filter is List<object> list && list.Count == 1 && Equals(list[0], "/FlateDecode"))) throw InvalidPdf("PDF 索引流过滤器不受支持。");
                bytes = TryInflate(bytes) ?? throw InvalidPdf("PDF 索引流无法解压。");
            }
            if (!dict.TryGetValue("DecodeParms", out var parameters)) return bytes;
            if (parameters is List<object> p && p.Count == 1) parameters = p[0];
            if (!(parameters is Dictionary<string, object> parms)) return bytes;
            var predictor = DictInt(parms, "Predictor", 1); if (predictor == 1) return bytes;
            var columns = DictInt(parms, "Columns", 1);
            if (predictor < 10 || predictor > 15 || DictInt(parms, "Colors", 1) != 1 || DictInt(parms, "BitsPerComponent", 8) != 8 || columns <= 0 || columns > 4096 || bytes.Length % (columns + 1) != 0) throw InvalidPdf("PDF 索引流预测器不受支持。");
            var result = new byte[bytes.Length / (columns + 1) * columns];
            for (int source = 0, target = 0; source < bytes.Length; source += columns + 1, target += columns)
            {
                var method = bytes[source]; if (method > 4) throw InvalidPdf("PDF PNG 预测器类型无效。");
                for (var x = 0; x < columns; x++)
                {
                    var left = x == 0 ? 0 : result[target + x - 1]; var up = target == 0 ? 0 : result[target + x - columns]; var corner = target == 0 || x == 0 ? 0 : result[target + x - columns - 1];
                    var estimate = left + up - corner; var a = Math.Abs(estimate - left); var b = Math.Abs(estimate - up); var c = Math.Abs(estimate - corner);
                    var prediction = method == 0 ? 0 : method == 1 ? left : method == 2 ? up : method == 3 ? (left + up) / 2 : a <= b && a <= c ? left : b <= c ? up : corner;
                    result[target + x] = unchecked((byte)(bytes[source + x + 1] + prediction));
                }
            }
            return result;
        }
    }
}
