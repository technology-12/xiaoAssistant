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
        public bool OcrUsed { get; }
        public double? OcrConfidence { get; }
        public bool OcrImageFallback { get; }

        public PdfPageText(int pageNumber, string text, bool truncated, bool ocrUsed = false, double? ocrConfidence = null, bool ocrImageFallback = false)
        {
            PageNumber = pageNumber;
            Text = text;
            Truncated = truncated;
            OcrUsed = ocrUsed; OcrConfidence = ocrConfidence; OcrImageFallback = ocrImageFallback;
        }
    }

    public sealed class PdfTextResult
    {
        public int PageCount { get; }
        public IList<PdfPageText> Pages { get; }
        public int TotalChars { get; }
        /// <summary>有页面资源但没有可提取文字 → 可能是扫描件（明确提示，不与空文档混淆）。</summary>
        public bool LikelyScanned { get; }
        public int OcrPages => Pages.Count(p => p.OcrUsed);
        public int[] LowConfidencePages => Pages.Where(p => p.OcrUsed && (p.OcrConfidence ?? 0) < .65).Select(p => p.PageNumber).ToArray();
        public string OcrNotice => OcrPages == 0 ? "正文来自 PDF 原有文字层。" : "其中 " + OcrPages + " 页由本地 OCR 识别，可能存在错字、漏字或阅读顺序偏差，请核对原页。" +
            (LowConfidencePages.Length == 0 ? "" : "低置信度页：" + string.Join("、", LowConfidencePages) + "。") +
            (Pages.Any(p => p.OcrImageFallback) ? "部分混合页采用图像层识别并单列原有文字层，请核对阅读顺序。" : "");

        public PdfTextResult(int pageCount, IList<PdfPageText> pages,
            int totalChars, bool likelyScanned)
        {
            PageCount = pageCount;
            Pages = pages;
            TotalChars = totalChars;
            LikelyScanned = likelyScanned;
        }
    }

    /// <summary>Managed PDF parser: page-tree order and per-font Unicode maps are provided by PdfPig.</summary>
    public static class PdfText
    {
        public const long MaxFileBytes = 10L * 1024 * 1024;
        public const int MaxCharsPerPage = 20000;
        public const int MaxTotalChars = 200000;
        public static PdfTextResult Extract(string fullPath, string relativePathForError, int maxPages = 200)
        {
            if (!File.Exists(fullPath) || new FileInfo(fullPath).Length > MaxFileBytes)
                throw new ToolException(ErrorCodes.LimitExceeded, "PDF 不存在或超过 10 MiB 限制：" + relativePathForError);
            try
            {
                using var document = UglyToad.PdfPig.PdfDocument.Open(fullPath);
                using var ocr = new PdfOcr(document, fullPath);
                var pages = new List<PdfPageText>(); var total = 0; bool hasImages = false;
                var count = Math.Min(Math.Min(Math.Max(1, maxPages), 200), document.NumberOfPages);
                for (var number = 1; number <= count; number++)
                {
                    DocumentLimits.Check();
                    var page = document.GetPage(number);
                    hasImages |= page.NumberOfImages > 0;
                    var text = page.Text;
                    // A large image may contain the actual text while a small
                    // existing text layer contains only a page number/header.
                    bool imageText = page.NumberOfImages > 0 && (text.Trim().Length < 80 || page.GetImages().Any(image =>
                        image.Bounds.Width * image.Bounds.Height >= page.Width * page.Height * .05));
                    var content = string.IsNullOrWhiteSpace(text) || imageText
                        ? ocr.Read(number, page.Width, page.Height) : new PdfPageText(number, text, false);
                    text = content.Text;
                    if (text.Length > MaxCharsPerPage)
                        throw new ToolException(ErrorCodes.LimitExceeded, "单页 PDF 文字超限，请拆分文件；不提供静默截断结果。 ");
                    total += text.Length;
                    if (total > MaxTotalChars) throw new ToolException(ErrorCodes.LimitExceeded, "PDF 文本总量超限。 ");
                    pages.Add(content);
                }
                return new PdfTextResult(document.NumberOfPages, pages, total, total == 0 && hasImages);
            }
            catch (ToolException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            { throw new ToolException(ErrorCodes.UnsupportedFormat, "PDF 无法可靠解析（可能损坏、加密或不支持）：" + relativePathForError, ex); }
        }
    }
}
