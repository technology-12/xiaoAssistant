using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public static class PresentationAutomation
    {
        static readonly XNamespace P = OfficePackage.P, A = OfficePackage.A, R = OfficePackage.R;
        const string Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
        public static int Units(string text) => text.Sum(c => c > 255 ? 2 : 1);
        static XElement Group() => new(P + "spTree", new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")), new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
            new XElement(P + "grpSpPr", new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0)), new XElement(A + "chOff", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "chExt", new XAttribute("cx", 0), new XAttribute("cy", 0)))));
        static XElement Shape(int id, string name, double x, double y, double width, double height, int font, string color, IEnumerable<string> lines, bool bold = false) =>
            new(P + "sp", new XElement(P + "nvSpPr", new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)), new XElement(P + "cNvSpPr", new XAttribute("txBox", 1)), new XElement(P + "nvPr")),
                new XElement(P + "spPr", new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", (long)(x * 914400)), new XAttribute("y", (long)(y * 914400))), new XElement(A + "ext", new XAttribute("cx", (long)(width * 914400)), new XAttribute("cy", (long)(height * 914400)))), new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")), new XElement(A + "noFill"), new XElement(A + "ln", new XElement(A + "noFill"))),
                new XElement(P + "txBody", new XElement(A + "bodyPr", new XAttribute("wrap", "square"), new XAttribute("lIns", 0), new XAttribute("rIns", 0), new XAttribute("tIns", 0), new XAttribute("bIns", 0), new XElement(A + "noAutofit")), new XElement(A + "lstStyle"),
                    lines.DefaultIfEmpty("").Select(text => new XElement(A + "p", new XElement(A + "pPr", new XElement(A + "lnSpc", new XElement(A + "spcPct", new XAttribute("val", 120000))), new XElement(A + "spcAft", new XElement(A + "spcPts", new XAttribute("val", 800)))),
                        new XElement(A + "r", new XElement(A + "rPr", new XAttribute("lang", "zh-CN"), new XAttribute("sz", font * 100), new XAttribute("b", bold ? 1 : 0), new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", color))), new XElement(A + "latin", new XAttribute("typeface", "Calibri")), new XElement(A + "ea", new XAttribute("typeface", "微软雅黑"))), new XElement(A + "t", text)), new XElement(A + "endParaRPr", new XAttribute("sz", font * 100))))));
        public static byte[] Create(JArray slides) => Create(slides, 200);
        public static byte[] Create(JArray slides, int maxSlides = 200)
        {
            if (slides.Count < 1 || slides.Count > maxSlides) throw new ToolException(ErrorCodes.LimitExceeded, "演示文稿页数超过当前资源预算：" + maxSlides);
            using var seed = typeof(PresentationAutomation).Assembly.GetManifestResourceStream("WorkAssistant.Tools.Assets.PresentationBase.pptx")!;
            using var archive = new ZipArchive(seed);
            var parts = archive.Entries.ToDictionary(e => e.FullName, e => { using var stream = e.Open(); return DocumentLimits.ReadBounded(stream); });
            var presentation = OfficePackage.Part(parts, "ppt/presentation.xml"); var relationships = OfficePackage.Part(parts, "ppt/_rels/presentation.xml.rels"); var types = OfficePackage.Part(parts, "[Content_Types].xml");
            var list = presentation.Root!.Element(P + "sldIdLst"); if (list == null) { list = new XElement(P + "sldIdLst"); presentation.Root.Element(P + "sldMasterIdLst")!.AddAfterSelf(list); } list.RemoveNodes();
            for (int i = 0; i < slides.Count; i++)
            {
                DocumentLimits.Check();
                if (slides[i] is not JObject slide) throw new ToolException(ErrorCodes.InvalidArguments, "slides 每项需为对象。");
                ToolArgs.RejectUnknown(slide, "title", "bullets", "layout", "notes");
                string title = OfficePackage.Field(slide, "title", 100), layout = ToolArgs.OptionalString(slide, "layout", 20) ?? "content";
                bool cover = layout == "cover";
                if (!cover && layout != "content") throw new ToolException(ErrorCodes.InvalidArguments, "layout 仅支持 cover/content。");
                if (title.Contains('\n') || title.Contains('\r') || Units(title) > (cover ? 80 : 54)) throw new ToolException(ErrorCodes.InvalidArguments, "标题需为短单段文本，请缩短以避免溢出。");
                var bullets = slide["bullets"] is JArray empty && empty.Count == 0 ? new List<string>() : ToolArgs.OptionalStringArray(slide, "bullets", cover ? 2 : 6) ?? new List<string>();
                if (bullets.Any(t => t.Length > 200 || t.Contains('\n') || t.Contains('\r')) || bullets.Sum(t => Math.Max(1, (Units(t) + 69) / 70)) > (cover ? 4 : 11))
                    throw new ToolException(ErrorCodes.InvalidArguments, "页面文字过密，请拆分为更多幻灯片。");
                string notes = ToolArgs.OptionalString(slide, "notes", 3000) ?? "";
                var tree = Group(); tree.Add(Shape(2, "Title", .85, cover ? 1.4 : .55, 11.5, cover ? 1.8 : .8, cover ? 36 : 28, cover ? "FFFFFF" : "20382D", new[] { title }, true));
                tree.Add(Shape(3, "Content", .95, cover ? 3.6 : 1.7, 11.1, cover ? 2.2 : 4.9, 20, cover ? "D9E8DF" : "34483C", bullets.Select(b => cover ? b : "• " + b)));
                tree.Add(Shape(4, "Page", 11.5, 7.04, .9, .28, 10, cover ? "C7DBCE" : "5E7566", new[] { (i + 1) + " / " + slides.Count }));
                var doc = new XDocument(new XElement(P + "sld", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "r", R), new XAttribute(XNamespace.Xmlns + "p", P),
                    new XElement(P + "cSld", new XElement(P + "bg", new XElement(P + "bgPr", new XElement(A + "solidFill", new XElement(A + "srgbClr", new XAttribute("val", cover ? "203C31" : "FAFBF8"))), new XElement(A + "effectLst"))), tree), new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping"))));
                string id = "xiaoSlide" + (i + 1), part = "ppt/slides/slide" + (i + 1) + ".xml";
                parts[part] = OfficePackage.Bytes(doc);
                parts["ppt/slides/_rels/slide" + (i + 1) + ".xml.rels"] = PackageZip.Utf8("<Relationships xmlns='" + Rel + "'><Relationship Id='rId1' Type='" + R + "/slideLayout' Target='../slideLayouts/slideLayout7.xml'/><Relationship Id='rId2' Type='" + R + "/notesSlide' Target='../notesSlides/notesSlide" + (i + 1) + ".xml'/></Relationships>");
                list.Add(new XElement(P + "sldId", new XAttribute("id", 256 + i), new XAttribute(R + "id", id)));
                relationships.Root!.Add(new XElement(XName.Get("Relationship", Rel), new XAttribute("Id", id), new XAttribute("Type", R.NamespaceName + "/slide"), new XAttribute("Target", "slides/slide" + (i + 1) + ".xml")));
                types.Root!.Add(new XElement(types.Root.Name.Namespace + "Override", new XAttribute("PartName", "/" + part), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml")));
                var notesTree = Group(); var noteShape = Shape(2, "Notes", 1, 1, 6, 7, 12, "000000", new[] { notes }); noteShape.Element(P + "nvSpPr")!.Element(P + "nvPr")!.Add(new XElement(P + "ph", new XAttribute("type", "body"), new XAttribute("idx", 1))); notesTree.Add(noteShape);
                parts["ppt/notesSlides/notesSlide" + (i + 1) + ".xml"] = OfficePackage.Bytes(new XDocument(new XElement(P + "notes", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "p", P), new XElement(P + "cSld", notesTree), new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping")))));
                parts["ppt/notesSlides/_rels/notesSlide" + (i + 1) + ".xml.rels"] = PackageZip.Utf8("<Relationships xmlns='" + Rel + "'><Relationship Id='rId1' Type='" + R + "/slide' Target='../slides/slide" + (i + 1) + ".xml'/><Relationship Id='rId2' Type='" + R + "/notesMaster' Target='../notesMasters/notesMaster1.xml'/></Relationships>");
                types.Root.Add(new XElement(types.Root.Name.Namespace + "Override", new XAttribute("PartName", "/ppt/notesSlides/notesSlide" + (i + 1) + ".xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml")));
            }
            presentation.Root!.Element(P + "sldSz")!.SetAttributeValue("cx", 12192000); presentation.Root.Element(P + "sldSz")!.SetAttributeValue("cy", 6858000); presentation.Root.Element(P + "sldSz")!.SetAttributeValue("type", "screen16x9");
            parts["ppt/presentation.xml"] = OfficePackage.Bytes(presentation); parts["ppt/_rels/presentation.xml.rels"] = OfficePackage.Bytes(relationships); parts["[Content_Types].xml"] = OfficePackage.Bytes(types);
            if (parts.ContainsKey("docProps/app.xml"))
            {
                var app = OfficePackage.Part(parts, "docProps/app.xml");
                foreach (var count in app.Root!.Elements().Where(e => e.Name.LocalName == "Slides" || e.Name.LocalName == "Notes")) count.Value = slides.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                parts["docProps/app.xml"] = OfficePackage.Bytes(app);
            }
            return OfficePackage.Pack(parts);
        }
        public static void Edit(Dictionary<string, byte[]> parts, JArray edits)
        {
            var slides = OfficePackage.SlideParts(parts); var seen = new HashSet<string>();
            foreach (var item in edits)
            {
                if (item is not JObject edit) throw new ToolException(ErrorCodes.InvalidArguments, "每项修改需为对象。");
                ToolArgs.RejectUnknown(edit, "slide_number", "shape_id", "paragraph_index", "expected_text", "new_text");
                int number = ToolArgs.OptionalIntInRange(edit, "slide_number", 0, 1, slides.Count), shapeId = ToolArgs.OptionalIntInRange(edit, "shape_id", 0, 1, int.MaxValue), index = ToolArgs.OptionalIntInRange(edit, "paragraph_index", 0, 1, 1000);
                if (number < 1 || shapeId < 1 || index < 1 || !seen.Add(number + ":" + shapeId + ":" + index)) throw new ToolException(ErrorCodes.InvalidArguments, "页面、形状或段落定位无效/重复。");
                var document = OfficePackage.Part(parts, slides[number - 1]);
                var shape = document.Descendants(P + "sp").SingleOrDefault(s => (int?)s.Element(P + "nvSpPr")?.Element(P + "cNvPr")?.Attribute("id") == shapeId) ?? throw new ToolException(ErrorCodes.InvalidArguments, "文本形状不存在。");
                var paragraphs = shape.Element(P + "txBody")?.Elements(A + "p").ToArray() ?? XpArray.Empty<XElement>();
                if (index > paragraphs.Length) throw new ToolException(ErrorCodes.InvalidArguments, "文本段落不存在。");
                var paragraph = paragraphs[index - 1]; string before = OfficePackage.Field(edit, "expected_text", 4000, true), after = OfficePackage.Field(edit, "new_text", 4000, true);
                if (before != OfficePackage.Text(paragraph, A)) throw new ToolException(ErrorCodes.EditConflict, "幻灯片文字已变化。");
                if (after == before || after.Contains('\n') || after.Contains('\r')) throw new ToolException(ErrorCodes.InvalidArguments, "替换需为不同的单段文本。");
                if (paragraph.Elements().Any(e => e.Name != A + "pPr" && e.Name != A + "r" && e.Name != A + "endParaRPr")) throw new ToolException(ErrorCodes.UnsupportedFormat, "不修改包含域或特殊换行的段落。");
                var runs = paragraph.Elements(A + "r").ToArray();
                if (runs.Length == 0) throw new ToolException(ErrorCodes.UnsupportedFormat, "空形状没有可继承的文本样式。");
                runs[0].Element(A + "t")!.Value = after; foreach (var run in runs.Skip(1)) run.Element(A + "t")!.Value = "";
                var extent = shape.Element(P + "spPr")?.Element(A + "xfrm")?.Element(A + "ext");
                if (extent != null)
                {
                    double width = (double)extent.Attribute("cx")! / 12700.0, height = (double)extent.Attribute("cy")! / 12700.0, used = 0;
                    foreach (var p in paragraphs)
                    {
                        double font = (double?)p.Element(A + "r")?.Element(A + "rPr")?.Attribute("sz") / 100 ?? 24;
                        used += Math.Max(1, Math.Ceiling(Units(OfficePackage.Text(p, A)) / Math.Max(1, width / (font * .55)))) * font * 1.2 + 8;
                    }
                    if (used > height + 6) throw new ToolException(ErrorCodes.InvalidArguments, "替换后可能超出文本框，请缩短文字或重新生成页面。");
                }
                parts[slides[number - 1]] = OfficePackage.Bytes(document);
            }
        }
    }
}
