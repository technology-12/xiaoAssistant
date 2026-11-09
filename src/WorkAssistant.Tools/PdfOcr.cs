using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using Tesseract;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Rendering.Skia;
using WorkAssistant.Core;
using SkiaSharp;
using UglyToad.PdfPig.Tokens;

namespace WorkAssistant.Tools
{
    // Runs inside the controlled document worker. Native recognition is bounded
    // by the parent worker deadline/cancellation; no remote OCR or shell process.
    internal sealed class PdfOcr : IDisposable
    {
        const string CacheVersion = "skia-0191-tess52-fast410-300dpi-v2";
        readonly PdfDocument _document;
        readonly string? _cache;
        TesseractEngine? _engine;
        bool _rendererReady;
        public PdfOcr(PdfDocument document, string source)
        {
            _document = document;
            string? root = DocumentLimits.PdfCacheDirectory;
            if (!string.IsNullOrEmpty(root))
            {
                using var stream = File.OpenRead(source); using var sha = SHA256.Create();
                string hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
                _cache = Path.Combine(root, CacheVersion, hash);
            }
        }

        public PdfPageText Read(int number, double width, double height)
        {
            DocumentLimits.BeginOcr();
            string? target = _cache == null ? null : Path.Combine(_cache, "page-" + number + ".json");
            if (target != null && File.Exists(target))
            {
                try
                {
                    if (new FileInfo(target).Length <= 200000)
                    {
                        using var cached = File.OpenRead(target);
                        var value = JObject.Parse(System.Text.Encoding.UTF8.GetString(DocumentLimits.ReadBounded(cached, 200000)));
                        string? text = (string?)value["text"]; double confidence = (double?)value["confidence"] ?? -1;
                        if ((int?)value["page"] == number && text != null && text.Length <= PdfText.MaxCharsPerPage && confidence >= 0 && confidence <= 1)
                        {
                            DocumentLimits.ReportProgress("PDF 第 " + number + "/" + _document.NumberOfPages + " 页：复用本地 OCR 结果。");
                            return new PdfPageText(number, text, false, true, confidence, (bool?)value["image_fallback"] ?? false);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is Newtonsoft.Json.JsonException || ex is FormatException || ex is InvalidCastException) { /* Recompute invalid cache. */ }
            }
            DocumentLimits.ReportProgress("PDF 第 " + number + "/" + _document.NumberOfPages + " 页：正在本地识别中文和英文……");
            try
            {
                // Bound page dimensions before allocating a native bitmap.
                if (double.IsNaN(width) || double.IsInfinity(width) || double.IsNaN(height) || double.IsInfinity(height) || width <= 0 || height <= 0 || width > 14400 || height > 14400)
                    throw new ToolException(ErrorCodes.LimitExceeded, "PDF 第 " + number + " 页尺寸异常，未执行 OCR。");
                float scale = (float)Math.Min(300d / 72, Math.Min(6000d / Math.Max(width, height), Math.Sqrt(12000000d / (width * height))));
                if (scale <= 0) throw new ToolException(ErrorCodes.LimitExceeded, "PDF 页面无法安全渲染。");
                if (_engine == null)
                {
                    // Avoid OpenMP oversubscription when two local agents OCR
                    // concurrently. This worker owns its process environment.
                    Environment.SetEnvironmentVariable("OMP_THREAD_LIMIT", "1");
                    string data = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ocr", "tessdata");
                    foreach (string language in new[] { "chi_sim", "chi_tra", "eng", "osd" })
                        if (!File.Exists(Path.Combine(data, language + ".traineddata")))
                            throw new ToolException(ErrorCodes.UnsupportedFormat, "本地 OCR 语言资源不完整，请重新解压完整软件包（ocr/tessdata）。");
                    _engine = new TesseractEngine(data, "chi_sim+chi_tra+eng", EngineMode.LstmOnly);
                    _engine.SetVariable("user_defined_dpi", "300");
                    _engine.SetVariable("preserve_interword_spaces", "1");
                }
                if (!_rendererReady) { _document.AddSkiaPageFactory(); _rendererReady = true; }
                bool imageFallback = false;
                using var png = Render(number, scale, width, height, out imageFallback);
                DocumentLimits.Check();
                using var image = Pix.LoadFromMemory(png.ToArray());
                using var page = _engine.Process(image, PageSegMode.AutoOsd);
                string text = page.GetText().Trim(); double confidence = page.GetMeanConfidence();
                if (imageFallback)
                {
                    string existing = _document.GetPage(number).Text.Trim();
                    if (existing.Length > 0) text = "[PDF 原有文字层]\n" + existing + "\n[图像 OCR]\n" + text;
                }
                DocumentLimits.Check();
                if (text.Length > PdfText.MaxCharsPerPage) throw new ToolException(ErrorCodes.LimitExceeded, "PDF 第 " + number + " 页 OCR 文字超限，请拆分文件；没有截断正文。");
                if (target != null)
                {
                    try
                    {
                        Directory.CreateDirectory(_cache!);
                        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            File.WriteAllText(temporary, new JObject { ["page"] = number, ["text"] = text, ["confidence"] = confidence, ["image_fallback"] = imageFallback }.ToString(Newtonsoft.Json.Formatting.None));
                            if (!File.Exists(target)) File.Move(temporary, target);
                            else File.Replace(temporary, target, null);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { /* Reading still succeeds if optional caching is unavailable. */ }
                }
                DocumentLimits.ReportProgress("PDF 第 " + number + "/" + _document.NumberOfPages + " 页：识别完成" + (confidence < .65 ? "，置信度较低，请核对原页。" : "。"));
                return new PdfPageText(number, text, false, true, confidence, imageFallback);
            }
            catch (ToolException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is DllNotFoundException || ex is TypeInitializationException || ex is BadImageFormatException || ex is TesseractException)
            {
                throw new ToolException(ErrorCodes.UnsupportedFormat, "PDF 第 " + number + " 页本地 OCR 初始化或识别失败；请使用完整解压包并运行“准备运行环境”，确认 VC++ 2019 运行库及 OCR 资源完整。", ex);
            }
        }
        MemoryStream Render(int number, float scale, double width, double height, out bool imageFallback)
        {
            imageFallback = false;
            var original = _document.GetPage(number);
            if (original.NumberOfImages > 0 && !string.IsNullOrWhiteSpace(original.Text))
            {
                imageFallback = true;
                return RenderImageLayer(number, scale, width, height);
            }
            try { return _document.GetPageAsPng(number, scale, RGBColor.White); }
            catch (InvalidOperationException)
            {
                // Some host fonts cannot be parsed by the upstream TrueType
                // fallback. Recognize the complete image layer and retain the
                // original text separately, rather than failing or dropping it.
                if (_document.GetPage(number).NumberOfImages == 0) throw;
                imageFallback = true;
                return RenderImageLayer(number, scale, width, height);
            }
        }
        MemoryStream RenderImageLayer(int number, float scale, double width, double height)
        {
                var pdfPage = _document.GetPage(number);
                var images = pdfPage.GetImages().ToArray();
                if (images.Length == 0 || images.Length > 200) throw new ToolException(ErrorCodes.LimitExceeded, "PDF 页面图像数量超出 OCR 限制。");
                using var bitmap = new SKBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale));
                using var canvas = new SKCanvas(bitmap); canvas.Clear(SKColors.White);
                foreach (var image in images)
                {
                    DocumentLimits.Check();
                    if ((long)image.WidthInSamples * image.HeightInSamples > 32000000 || image.WidthInSamples <= 0 || image.HeightInSamples <= 0)
                        throw new ToolException(ErrorCodes.LimitExceeded, "PDF 图像尺寸超出本地 OCR 解码预算。");
                    DocumentLimits.ReportProgress("PDF 第 " + number + " 页：正在解码扫描图像……");
                    using var decoded = DecodeImage(image);
                    if (decoded == null) throw new ToolException(ErrorCodes.UnsupportedFormat, "PDF 第 " + number + " 页图像无法解码，未将不完整图像当作全文。");
                    var b = image.Bounds;
                    var matrix = new SKMatrix {
                        ScaleX = (float)((b.TopRight.X - b.TopLeft.X) * scale / decoded.Width),
                        SkewX = (float)((b.BottomLeft.X - b.TopLeft.X) * scale / decoded.Height),
                        TransX = (float)(b.TopLeft.X * scale),
                        SkewY = (float)(-(b.TopRight.Y - b.TopLeft.Y) * scale / decoded.Width),
                        ScaleY = (float)(-(b.BottomLeft.Y - b.TopLeft.Y) * scale / decoded.Height),
                        TransY = (float)((height - b.TopLeft.Y) * scale), Persp2 = 1
                    };
                    canvas.Save(); canvas.SetMatrix(matrix); canvas.DrawBitmap(decoded, 0, 0); canvas.Restore();
                }
                using var rendered = SKImage.FromBitmap(bitmap); using var data = rendered.Encode(SKEncodedImageFormat.Png, 100);
                DocumentLimits.ReportProgress("PDF 第 " + number + " 页：使用图像层 OCR，并保留原有文字层；阅读顺序请核对。");
                return new MemoryStream(data.ToArray(), false);
        }
        static SKBitmap? DecodeImage(UglyToad.PdfPig.Content.IPdfImage image)
        {
            var color = image.ColorSpaceDetails?.Type;
            bool basicDecode = image.Decode.Count == 0 || image.Decode.Select((value, index) => value == index % 2).All(v => v);
            bool masked = image.ImageDictionary.Data.ContainsKey("SMask") || image.ImageDictionary.Data.ContainsKey("Mask");
            if (image.BitsPerComponent == 8 && basicDecode && !masked &&
                (color == ColorSpace.DeviceRGB || color == ColorSpace.DeviceGray) && ReadBasicPixels(image, color == ColorSpace.DeviceRGB ? 3 : 1, out var raw))
            {
                int channels = color == ColorSpace.DeviceRGB ? 3 : 1;
                int pixels = checked(image.WidthInSamples * image.HeightInSamples);
                if (raw.Length == pixels * channels)
                {
                    // Avoid a managed RGB -> PNG -> native RGB roundtrip for
                    // large Flate/ASCII85 scans. The worker bounds decoded size.
                    var data = raw.Span; var bgra = new byte[checked(pixels * 4)];
                    for (int i = 0; i < pixels; i++)
                    {
                        if (i % 65536 == 0) DocumentLimits.Check();
                        int source = i * channels, dest = i * 4;
                        bgra[dest] = data[source + (channels == 3 ? 2 : 0)];
                        bgra[dest + 1] = data[source + (channels == 3 ? 1 : 0)];
                        bgra[dest + 2] = data[source]; bgra[dest + 3] = 255;
                    }
                    var bitmap = new SKBitmap(image.WidthInSamples, image.HeightInSamples, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    try { System.Runtime.InteropServices.Marshal.Copy(bgra, 0, bitmap.GetPixels(), bgra.Length); return bitmap; }
                    catch { bitmap.Dispose(); throw; }
                }
            }
            byte[] bytes = image.TryGetPng(out var converted) ? converted : image.RawBytes.ToArray();
            return SKBitmap.Decode(bytes);
        }
        static bool ReadBasicPixels(UglyToad.PdfPig.Content.IPdfImage image, int channels, out ReadOnlyMemory<byte> raw)
        {
            // PdfPig 0.1.9's ASCII85 path becomes very expensive on large RGB
            // scans. Decode this common, unpredicted filter chain linearly.
            // Keep all other color/filter/predictor cases in the library.
            var dictionary = image.ImageDictionary.Data;
            if (!dictionary.ContainsKey("DecodeParms") && dictionary.TryGetValue("Filter", out var token) && token is ArrayToken array)
            {
                var filters = array.Data.Select(t => (t as NameToken)?.Data).ToArray();
                if (filters.Length == 2 && (filters[0] == "ASCII85Decode" || filters[0] == "A85") && (filters[1] == "FlateDecode" || filters[1] == "Fl"))
                {
                    byte[] encoded = image.RawBytes.ToArray();
                    using var packed = new MemoryStream(); ulong tuple = 0; int digits = 0; bool terminated = false;
                    for (int i = 0; i < encoded.Length; i++)
                    {
                        if (i % 65536 == 0) DocumentLimits.Check();
                        byte ch = encoded[i];
                        if (ch == 0 || ch == 9 || ch == 10 || ch == 12 || ch == 13 || ch == 32) continue;
                        if (terminated) throw new FormatException("Unexpected data after ASCII85 terminator.");
                        if (ch == '~')
                        {
                            if (++i >= encoded.Length || encoded[i] != '>') throw new FormatException("Invalid ASCII85 terminator.");
                            terminated = true; continue;
                        }
                        if (ch == 'z')
                        {
                            if (digits != 0) throw new FormatException("Invalid ASCII85 zero group.");
                            packed.Write(new byte[4], 0, 4); continue;
                        }
                        if (ch < 33 || ch > 117) throw new FormatException("Invalid ASCII85 digit.");
                        tuple = tuple * 85 + ch - 33; digits++;
                        if (digits == 5)
                        {
                            if (tuple > uint.MaxValue) throw new FormatException("ASCII85 group overflow.");
                            for (int shift = 24; shift >= 0; shift -= 8) packed.WriteByte((byte)(tuple >> shift));
                            tuple = 0; digits = 0;
                        }
                    }
                    if (digits == 1 || !terminated) throw new FormatException("Incomplete ASCII85 stream.");
                    if (digits > 1)
                    {
                        int bytes = digits - 1;
                        while (digits++ < 5) tuple = tuple * 85 + 84;
                        if (tuple > uint.MaxValue) throw new FormatException("ASCII85 tail overflow.");
                        for (int i = 0; i < bytes; i++) packed.WriteByte((byte)(tuple >> (24 - 8 * i)));
                    }
                    byte[] zlib = packed.ToArray();
                    if (zlib.Length < 6 || (zlib[0] & 15) != 8 || (zlib[1] & 32) != 0 || (zlib[0] * 256 + zlib[1]) % 31 != 0) throw new FormatException("Invalid zlib header.");
                    using var input = new MemoryStream(zlib, 2, zlib.Length - 6, false);
                    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                    byte[] pixels = DocumentLimits.ReadBounded(deflate, (long)image.WidthInSamples * image.HeightInSamples * channels);
                    uint a = 1, b = 0;
                    for (int offset = 0; offset < pixels.Length; offset += 5552)
                    {
                        DocumentLimits.Check();
                        int end = Math.Min(offset + 5552, pixels.Length);
                        for (int i = offset; i < end; i++) { a += pixels[i]; b += a; }
                        a %= 65521; b %= 65521;
                    }
                    int n = zlib.Length;
                    uint expected = ((uint)zlib[n - 4] << 24) | ((uint)zlib[n - 3] << 16) | ((uint)zlib[n - 2] << 8) | zlib[n - 1];
                    if ((b << 16 | a) != expected) throw new FormatException("Image zlib checksum mismatch.");
                    raw = pixels; return true;
                }
            }
            return image.TryGetBytesAsMemory(out raw);
        }
        public void Dispose() { _engine?.Dispose(); }
    }
}
