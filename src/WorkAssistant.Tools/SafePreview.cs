using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public sealed class PreviewDocument
    {
        public string Text { get; set; } = "";
        public string[] Columns { get; set; } = XpArray.Empty<string>();
        public string[][] Rows { get; set; } = XpArray.Empty<string[]>();
        public string Notice { get; set; } = "";
    }
    public static class SafePreview
    {
        public static PreviewDocument Read(TaskRecord task, string relative, string expectedHash, CancellationToken token)
        {
            DocumentLimits.Begin(token);
            var policy = new WorkspacePolicy(task.WorkspaceRoot);
            bool published = task.Artifacts.Any(a => a.PublishedPath == relative && a.Sha256 == expectedHash && relative.StartsWith(WorkspacePolicy.DeliveryDir + "/", StringComparison.Ordinal));
            string full = published ? policy.ResolveDeliveryPath(Path.GetFileName(relative)) : policy.ResolveTaskOutputPath(task.TaskId, Path.GetFileName(relative));
            if (!string.Equals(policy.ToWorkspaceRelative(full), relative, StringComparison.OrdinalIgnoreCase)) throw new IOException("预览路径不属于任务成果。");
            using var locked = VerifiedFiles.OpenRead(full, Path.GetDirectoryName(full)!);
            if (locked.Length > 10L * 1024 * 1024 || TaskOutputs.Sha256File(full) != expectedHash) throw new IOException("成果已变化或超过预览上限。");
            string extension = Path.GetExtension(full).ToLowerInvariant();
            if (extension == ".pptx")
            {
                var parts = OfficePackage.Read(full); var slides = OfficePackage.SlideParts(parts);
                var text = string.Join("\n\n", slides.Take(40).Select((slide, i) => "第 " + (i + 1) + " 页\n" + string.Join("\n", OfficePackage.Part(parts, slide).Descendants(OfficePackage.A + "p").Select(p => OfficePackage.Text(p, OfficePackage.A)))));
                return new PreviewDocument { Text = text.Length > 100000 ? text.Substring(0, 100000) : text, Notice = "PPTX 文字预览，最多 40 页；这不是幻灯片的实际渲染。" };
            }
            if (TableReading.IsSupported(full))
            {
                var table = TableReading.Read(full, relative);
                return new PreviewDocument { Columns = table.Header, Rows = table.Rows.Take(200).ToArray(), Notice = $"共 {table.Rows.Count} 行；预览前 200 行。" };
            }
            if (extension == ".docx")
            {
                PackageZip.ValidatePassiveTemplate(full);
                var xml = PackageZip.ReadEntry(full, "word/document.xml") ?? throw new IOException("文档缺少主部件。");
                using var stream = new MemoryStream(xml); using var reader = DocumentLimits.Xml(stream);
                var document = XDocument.Load(reader);
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                string text = string.Join("\n", document.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))));
                return new PreviewDocument { Text = text.Length > 100000 ? text.Substring(0, 100000) : text, Notice = "DOCX 纯文本预览，保留原件排版；最多显示 10 万字符。" };
            }
            if (extension == ".pdf")
            {
                var pdf = PdfText.Extract(full, relative, 5);
                return new PreviewDocument { Text = string.Join("\n\n", pdf.Pages.Select(p => "第 " + p.PageNumber + " 页\n" + p.Text)), Notice = "PDF 文字预览，最多显示前 5 页。" + pdf.OcrNotice };
            }
            if (extension != ".txt" && extension != ".md" && extension != ".json" && extension != ".diff") throw new IOException("此类型请使用本地应用打开。");
            byte[] bytes = new byte[(int)Math.Min(locked.Length, 300000)];
            int offset = 0, read;
            while (offset < bytes.Length && (read = locked.Read(bytes, offset, bytes.Length - offset)) > 0) { token.ThrowIfCancellationRequested(); offset += read; }
            Encoding encoding = Encoding.UTF8; int skip = 0;
            if (offset >= 2 && bytes[0] == 255 && bytes[1] == 254) { encoding = Encoding.Unicode; skip = 2; }
            else if (offset >= 2 && bytes[0] == 254 && bytes[1] == 255) { encoding = Encoding.BigEndianUnicode; skip = 2; }
            else if (offset >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) skip = 3;
            return new PreviewDocument { Text = encoding.GetString(bytes, skip, offset - skip), Notice = extension == ".diff"
                ? "文本差异预览，最多 300 KB；提案不等于已提交的修改。" : "纯文本预览，最多 300 KB；未执行文档内容。" };
        }
    }
}
