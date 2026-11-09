using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public static class OfficePackage
    {
        public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        public static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        public static Dictionary<string, byte[]> Read(string path)
        {
            if (new FileInfo(path).Length > 20L * 1024 * 1024) throw new ToolException(ErrorCodes.LimitExceeded, "Office 文件超过 20 MiB。");
            PackageZip.ValidatePassiveTemplate(path);
            using var zip = ZipFile.OpenRead(path); PackageZip.ValidateArchive(zip);
            var parts = zip.Entries.ToDictionary(e => e.FullName, e => { using var stream = PackageZip.OpenEntry(e); return DocumentLimits.ReadBounded(stream); }, StringComparer.Ordinal);
            var root = Part(parts, "_rels/.rels");
            var main = root.Root!.Elements().Where(e => (string?)e.Attribute("Type") == R.NamespaceName + "/officeDocument").ToArray();
            if (main.Length != 1) throw new ToolException(ErrorCodes.UnsupportedFormat, "Office 主部件关系无效。");
            string target = (string?)main[0].Attribute("Target") ?? "";
            var uri = new Uri(new Uri("https://local.invalid/"), target);
            string part = uri.AbsolutePath.TrimStart('/');
            if (uri.Host != "local.invalid" || (part != "word/document.xml" && part != "ppt/presentation.xml") || !parts.ContainsKey(part)) throw new ToolException(ErrorCodes.UnsupportedFormat, "Office 主部件路径无效。");
            string expected = part.StartsWith("word/", StringComparison.Ordinal) ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml" : "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
            if (!Part(parts, "[Content_Types].xml").Root!.Elements().Any(e => (string?)e.Attribute("PartName") == "/" + part && (string?)e.Attribute("ContentType") == expected)) throw new ToolException(ErrorCodes.UnsupportedFormat, "仅支持普通 DOCX/PPTX 主部件类型。");
            return parts;
        }
        public static XDocument Xml(byte[] bytes)
        { using var stream = new MemoryStream(bytes); using var reader = DocumentLimits.Xml(stream); return XDocument.Load(reader, LoadOptions.PreserveWhitespace); }
        public static byte[] Bytes(XDocument document) => Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        public static byte[] Pack(Dictionary<string, byte[]> parts) => PackageZip.WriteDeterministic(parts.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray());
        public static XDocument Part(Dictionary<string, byte[]> parts, string name) => parts.TryGetValue(name, out var bytes) ? Xml(bytes) : throw new ToolException(ErrorCodes.UnsupportedFormat, "Office 缺少部件：" + name);
        public static string Text(XElement paragraph, XNamespace ns) => string.Concat(paragraph.Descendants(ns + "t").Where(t => t.Ancestors(ns + "p").FirstOrDefault() == paragraph).Select(t => t.Value));
        public static List<XElement> WordParagraphs(XDocument document) => document.Root!.Element(W + "body")!.Descendants(W + "p").ToList();
        public static List<string> SlideParts(Dictionary<string, byte[]> parts)
        {
            var presentation = Part(parts, "ppt/presentation.xml");
            var relationships = Part(parts, "ppt/_rels/presentation.xml.rels").Root!.Elements().ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!);
            var result = new List<string>();
            foreach (var slide in presentation.Descendants(P + "sldId"))
            {
                if (!relationships.TryGetValue((string)slide.Attribute(R + "id")!, out string target)) throw new ToolException(ErrorCodes.UnsupportedFormat, "幻灯片关系不存在。");
                var uri = new Uri(new Uri("https://local.invalid/ppt/presentation.xml"), target);
                string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
                if (uri.Host != "local.invalid" || !path.StartsWith("ppt/slides/", StringComparison.Ordinal) || !parts.ContainsKey(path)) throw new ToolException(ErrorCodes.UnsupportedFormat, "幻灯片关系路径无效。");
                result.Add(path);
            }
            if (result.Count < 1 || result.Count > 100) throw new ToolException(ErrorCodes.LimitExceeded, "幻灯片数需为 1~100。");
            return result;
        }
        public static void RequireHash(string full, string expected)
        { if (TaskOutputs.Sha256File(full) != expected) throw new ToolException(ErrorCodes.EditConflict, "Office 文件版本与检查结果不一致，请重新检查。"); }
        public static string Field(Newtonsoft.Json.Linq.JObject obj, string key, int limit = 4000, bool empty = false)
        {
            if (obj[key]?.Type != Newtonsoft.Json.Linq.JTokenType.String) throw new ToolException(ErrorCodes.InvalidArguments, key + " 必须是字符串。");
            string text = (string)obj[key]!;
            if ((!empty && string.IsNullOrWhiteSpace(text)) || text.Length > limit || text.Any(c => char.IsControl(c) && c != '\n' && c != '\t' && c != '\r')) throw new ToolException(ErrorCodes.InvalidArguments, key + " 为空、超长或含控制字符。");
            return text;
        }
    }
}
