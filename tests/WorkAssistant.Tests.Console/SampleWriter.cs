using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using WorkAssistant.Tools;
using System.Threading.Tasks; // XP 线：GetAwaiter 扩展需要该导入

namespace WorkAssistant.Tests.Console
{
    /// <summary>
    /// 演示样例生成（dogfood：XLSX 用产品自身的写出器构造，PDF 手工构造两页样例：
    /// 第 1 页 WinAnsi 字面文本，第 2 页十六进制 CID + ToUnicode CMap 中文）。
    /// 用法：WorkAssistant.Tests.Console.exe --write-samples samples/workspace/inputs
    /// </summary>
    public static class SampleWriter
    {
        public static void WriteAll(string directory)
        {
            Directory.CreateDirectory(directory);
            WriteSalesXlsx(Path.Combine(directory, "sales.xlsx"));
            WriteProductPdf(Path.Combine(directory, "产品说明.pdf"));
        }

        /// <summary>销售 XLSX：与 sales-a/b.csv 同口径，总额 371.00。</summary>
        public static void WriteSalesXlsx(string path)
        {
            var header = new List<string> { "部门", "销售额" };
            var rows = new List<IList<XlsxCell>>
            {
                new[] { XlsxCell.Text2("销售一部"), XlsxCell.Number(100.10m) },
                new[] { XlsxCell.Text2("销售二部"), XlsxCell.Number(200.20m) },
                new[] { XlsxCell.Text2("销售一部"), XlsxCell.Number(50.30m) },
                new[] { XlsxCell.Text2("销售二部"), XlsxCell.Number(20.40m) }
            };
            File.WriteAllBytes(path, XlsxWriting.Build("销售明细", header, rows));
        }

        /// <summary>两页 PDF：英文页（flate 压缩字面文本）+ 中文页（hex + ToUnicode）。</summary>
        public static void WriteProductPdf(string path)
        {
            File.WriteAllBytes(path, BuildTwoPagePdf());
        }

        // ---- 最小 PDF 构造 ---------------------------------------------------

        public static byte[] BuildTwoPagePdf()
        {
            var page1Content = "BT\n/F1 14 Tf\n72 720 Td\n(Win7 Work Assistant) Tj\n0 -22 Td\n(Product manual, page one.) Tj\nET";
            var page2Content = "BT\n/F2 14 Tf\n72 720 Td\n<0041004200430044> Tj\n0 -22 Td\n<004500460047> Tj\nET";
            // ToUnicode CMap：bfchar（你好世界）+ bfrange 数组形式（助手册）。
            var cmap = "/CIDInit /ProcSet findresource begin\n" +
                       "12 dict begin\nbegincmap\n" +
                       "/CIDSystemInfo <</Registry (Adobe) /Ordering (UCS) /Supplement 0>> def\n" +
                       "/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n" +
                       "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
                       "4 beginbfchar\n" +
                       "<0041> <4F60>\n" + // 你
                       "<0042> <597D>\n" + // 好
                       "<0043> <4E16>\n" + // 世
                       "<0044> <754C>\n" + // 界
                       "endbfchar\n" +
                       "1 beginbfrange\n" +
                       "<0045> <0047> [<52A9> <624B> <518C>]\n" + // 助 手 册
                       "endbfrange\n" +
                       "endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend";

            var objects = new Dictionary<int, byte[]>();
            objects[1] = Latin1("<</Type /Catalog /Pages 2 0 R>>");
            objects[2] = Latin1("<</Type /Pages /Kids [3 0 R 5 0 R] /Count 2>>");
            objects[3] = Latin1("<</Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R " +
                                "/Resources <</Font <</F1 7 0 R>>>>>>");
            objects[4] = FlateStream(page1Content);
            objects[5] = Latin1("<</Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 6 0 R " +
                                "/Resources <</Font <</F2 8 0 R>>>>>>");
            objects[6] = FlateStream(page2Content);
            objects[7] = Latin1("<</Type /Font /Subtype /Type1 /BaseFont /Helvetica>>");
            objects[8] = Latin1("<</Type /Font /Subtype /Type0 /BaseFont /TestCJK /Encoding /Identity-H " +
                                "/DescendantFonts [10 0 R] /ToUnicode 9 0 R>>");
            objects[9] = FlateStream(cmap);
            objects[10] = Latin1("<</Type /Font /Subtype /CIDFontType2 /BaseFont /TestCJK /CIDSystemInfo <</Registry (Adobe) /Ordering (Identity) /Supplement 0>> /FontDescriptor 11 0 R /DW 1000 /CIDToGIDMap /Identity>>");
            objects[11] = Latin1("<</Type /FontDescriptor /FontName /TestCJK /Flags 4 /FontBBox [0 -200 1000 1000] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 700 /StemV 80>>");

            return AssemblePdf(objects, rootObject: 1);
        }

        /// <summary>无文字 PDF（含图像 XObject）：用于扫描件识别测试。</summary>
        public static byte[] BuildScannedLikePdf()
        {
            var objects = new Dictionary<int, byte[]>();
            objects[1] = Latin1("<</Type /Catalog /Pages 2 0 R>>");
            objects[2] = Latin1("<</Type /Pages /Kids [3 0 R] /Count 1>>");
            objects[3] = Latin1("<</Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
                                "/Resources <</XObject <</Im0 5 0 R>> /Font <</F1 7 0 R>>>>>>");
            objects[4] = Latin1(""); // 无内容流（扫描件常见：页面内容在图像中）
            objects[5] = Latin1("<</Type /XObject /Subtype /Image /Width 8 /Height 8 /ColorSpace /DeviceGray " +
                                "/BitsPerComponent 8 /Length 0 /Filter /DCTDecode>>");
            objects[7] = Latin1("<</Type /Font /Subtype /Type1 /BaseFont /Helvetica>>");
            return AssemblePdf(objects, rootObject: 1, includeEmptyContent: false);
        }

        private static byte[] Latin1(string text) => Encoding.GetEncoding(28591).GetBytes(text);

        /// <summary>FlateDecode 流对象（zlib 头 + deflate）。</summary>
        private static byte[] FlateStream(string content)
        {
            var raw = Encoding.GetEncoding(28591).GetBytes(content);
            using var deflate = new MemoryStream();
            using (var compressor = XpCompression.CreateDeflater(deflate, leaveOpen: true))
            {
                compressor.Write(raw, 0, raw.Length);
            }
            var body = deflate.ToArray();
            var obj = new MemoryStream();
            var writer = new BinaryWriter(obj);
            writer.Write(Latin1("<</Length " + (body.Length + 6) + " /Filter /FlateDecode>>\nstream\n"));
            writer.Write(new byte[] { 0x78, 0x9C }); // zlib 头
            writer.Write(body);
            uint a = 1, b = 0;
            foreach (var value in raw) { a = (a + value) % 65521; b = (b + a) % 65521; }
            var checksum = (b << 16) | a;
            writer.Write(new[] { (byte)(checksum >> 24), (byte)(checksum >> 16), (byte)(checksum >> 8), (byte)checksum });
            writer.Write(Latin1("\nendstream"));
            writer.Flush();
            return obj.ToArray();
        }

        private static byte[] AssemblePdf(Dictionary<int, byte[]> objects, int rootObject,
            bool includeEmptyContent = true)
        {
            var pdf = new MemoryStream();
            var writer = new BinaryWriter(pdf);
            writer.Write(Latin1("%PDF-1.4\n"));
            var offsets = new Dictionary<int, long>();
            foreach (var entry in objects)
            {
                if (!includeEmptyContent && entry.Value.Length == 0) continue;
                offsets[entry.Key] = pdf.Position;
                writer.Write(Latin1(entry.Key + " 0 obj\n"));
                writer.Write(entry.Value);
                writer.Write(Latin1("\nendobj\n"));
            }
            var xrefOffset = pdf.Position;
            var maxObject = 0;
            foreach (var key in objects.Keys) maxObject = Math.Max(maxObject, key);
            writer.Write(Latin1("xref\n0 " + (maxObject + 1) + "\n"));
            writer.Write(Latin1("0000000000 65535 f \n"));
            for (var i = 1; i <= maxObject; i++)
            {
                if (offsets.TryGetValue(i, out var offset))
                {
                    writer.Write(Latin1(offset.ToString("0000000000") + " 00000 n \n"));
                }
                else
                {
                    writer.Write(Latin1("0000000000 65535 f \n"));
                }
            }
            writer.Write(Latin1("trailer\n<</Size " + (maxObject + 1) + " /Root " + rootObject + " 0 R>>\n" +
                                 "startxref\n" + xrefOffset + "\n%%EOF"));
            writer.Flush();
            return pdf.ToArray();
        }
    }
}
