using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    public sealed class OfficeAutomationTool : AgentToolBase
    {
        readonly WorkspacePolicy _policy;
        readonly string _mode;
        public OfficeAutomationTool(WorkspacePolicy policy, string mode)
        {
            _policy = policy; _mode = mode;
            Definition = mode switch {
                "inspect_office" => new ToolDefinition(mode, "分页读取 DOCX/PPTX 的正文段落定位和源文件 SHA-256。修改前先检查，使用返回的段落/页面/形状编号与原文。Word 含表格段落；PPT 为普通文本形状，不含图表/SmartArt/OCR。",
                    Schema(("path", "string", "输入 Office 文件的 file_id；与 artifact_name 二选一", false), ("artifact_name", "string", "本任务已生成的 .docx/.pptx 成果文件名", false), ("start_item", "integer", "从 1 开始，默认 1", false), ("max_items", "integer", "1~60，默认 30", false), ("start_char", "integer", "超长段落的字符偏移，默认0；使用返回 next_char", false))),
                "write_word" => new ToolDefinition(mode, "自由生成 Word 新闻稿、教案、方案等 .docx；无需表格聚合。blocks 为 [{type:'heading'|'paragraph'|'bullet',text:'...'} 或 {type:'table',rows:[[单元格文本,...],...]}]。标题居中、正文中文字体和段落间距、表格表头样式。只写任务输出，不修改输入。",
                    Schema(("title", "string", "文档标题，最多 100 字", true), ("blocks", "array", "1~200 个正文块，单块文本最多 4000 字；表格最多 80 行6列", true), ("file_name", "string", "输出文件名，以 .docx 结尾", true)), true),
                "write_presentation" => new ToolDefinition(mode, "生成可编辑的 16:9 PPTX。slides 每项 {title,layout:'cover'或'content',bullets:[纯文本],notes:讲稿}。标题短、正文每页最多6点；文字太密会拒绝，请分更多页。包含讲者备注和页码，不生成图表/图像。只写任务成果。",
                    Schema(("slides", "array", "1~40 页，title 必填，bullets/layout/notes 可选", true), ("file_name", "string", "输出文件名，以 .pptx 结尾", true)), true),
                "edit_word" => new ToolDefinition(mode, "按 inspect_office 返回的 SHA-256 和正文段落编号修改 Word，输出新副本与修改清单，原件不变。edits:[{paragraph_index,expected_text,new_text}]。保留未修改部件；目标仅纯文本段落，新文本继承首个文字片段样式。track_changes 默认 true，保留删除/插入修订；不修改图像、域、链接或已有修订段落。",
                    Schema(("path", "string", "Word 输入 file_id；与 artifact_name 二选一", false), ("artifact_name", "string", "本任务已生成的 Word 成果文件名，用于继续编辑自己的输出", false), ("source_sha256", "string", "inspect_office 返回的文件 SHA-256", true), ("edits", "array", "1~50 个段落修改，段落原文精确匹配", true), ("track_changes", "boolean", "默认 true；false 输出直接替换的新副本", false), ("file_name", "string", "新的 .docx 文件名", true)), true),
                "edit_presentation" => new ToolDefinition(mode, "按 inspect_office 定位修改已有 PPTX 的普通文字，输出新副本与修改清单，保留原件、未修改页面及其他部件。edits:[{slide_number,shape_id,paragraph_index,expected_text,new_text}]。继承首个文字片段样式，支持已知文本框容量预检；占位符或复杂排版需人工核对，不编辑图表/SmartArt。",
                    Schema(("path", "string", "PPTX 输入 file_id；与 artifact_name 二选一", false), ("artifact_name", "string", "本任务已生成的 PPT 成果文件名", false), ("source_sha256", "string", "inspect_office 返回的文件 SHA-256", true), ("edits", "array", "1~50 个文本段落修改", true), ("file_name", "string", "新的 .pptx 文件名", true)), true),
                _ => throw new ArgumentException("Unknown office mode") };
            foreach (var property in ((JObject)Definition.Parameters["properties"]!).Properties().Where(p => p.Value["type"]?.ToString() == "array"))
                ((JObject)property.Value)["items"] = new JObject { ["type"] = "object" };
            if (mode == "write_word")
            {
                var fields = (JObject)Definition.Parameters["properties"]!;
                fields["title"]!["maxLength"] = 100;
                fields["blocks"]!["minItems"] = 0; fields["blocks"]!["maxItems"] = 200;
                fields["blocks"]!["items"] = new JObject {
                    ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("type"),
                    ["properties"] = new JObject {
                        ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray("heading", "paragraph", "bullet", "table") },
                        ["text"] = new JObject { ["type"] = "string", ["maxLength"] = 4000, ["description"] = "heading/paragraph/bullet 必填" },
                        ["rows"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 80, ["description"] = "table 必填，各行等宽",
                            ["items"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 6, ["items"] = new JObject { ["type"] = "string", ["maxLength"] = 1200 } } }
                    } };
                fields["draft_id"] = new JObject { ["type"] = "string", ["pattern"] = "^[A-Za-z0-9_-]{1,40}$", ["description"] = "长文稿分批时使用稳定草稿ID。每批建议≤12个块或2500字；标题、文件名保持一致。" };
                fields["part_index"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 2000, ["description"] = "草稿批次，从0开始；随后使用返回的 next_part_index。最多2000批；整份由可配置的文档资源预算约束。重试同批不能修改内容。" };
                fields["finalize"] = new JObject { ["type"] = "boolean", ["description"] = "分批草稿仅最后一批设true；此前无可交付文件。可用空blocks完成已有草稿。未使用draft_id时一次生成。" };
            }
        }
        public static JObject WordBlockSchema() => new JObject {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JArray("type"),
            ["properties"] = new JObject {
                ["type"] = new JObject { ["type"] = "string", ["enum"] = new JArray("heading", "paragraph", "bullet", "table") },
                ["text"] = new JObject { ["type"] = "string", ["maxLength"] = 4000 },
                ["rows"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 80,
                    ["items"] = new JObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 6, ["items"] = new JObject { ["type"] = "string", ["maxLength"] = 1200 } } }
            } };
        public override ToolDefinition Definition { get; }
        protected override string FallbackErrorCode => ErrorCodes.UnsupportedFormat;
        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context, JObject args, CancellationToken token)
        {
            if (_mode == "inspect_office")
            {
                ToolArgs.RejectUnknown(args, "path", "artifact_name", "start_item", "max_items", "start_char");
                if ((args["path"] != null) == (args["artifact_name"] != null)) throw new ToolException(ErrorCodes.InvalidArguments, "path 与 artifact_name 必须且只能提供一个。");
                string path = args["path"] != null ? _policy.ResolveReadablePath(ToolArgs.RequireString(args, "path")) : _policy.ResolveTaskOutputPath(context.TaskId, ToolArgs.RequireString(args, "artifact_name"));
                using var locked = VerifiedFiles.OpenRead(path, args["path"] != null ? _policy.SnapshotRoot ?? _policy.RootPath : Path.GetDirectoryName(path)!);
                var parts = OfficePackage.Read(path);
                int start = ToolArgs.OptionalIntInRange(args, "start_item", 1, 1, 50000), count = ToolArgs.OptionalIntInRange(args, "max_items", 30, 1, 60);
                var items = new List<JObject>(); string kind = Path.GetExtension(path).ToLowerInvariant();
                if (kind == ".docx")
                {
                    var paragraphs = OfficePackage.WordParagraphs(OfficePackage.Part(parts, "word/document.xml"));
                    for (int i = 0; i < paragraphs.Count; i++) items.Add(new JObject { ["paragraph_index"] = i + 1, ["text"] = OfficePackage.Text(paragraphs[i], OfficePackage.W),
                        ["editable_plain_text"] = paragraphs[i].Elements().All(e => e.Name == OfficePackage.W + "pPr" || e.Name == OfficePackage.W + "r") });
                }
                else if (kind == ".pptx")
                {
                    var slides = OfficePackage.SlideParts(parts);
                    for (int i = 0; i < slides.Count; i++)
                        foreach (var shape in OfficePackage.Part(parts, slides[i]).Descendants(OfficePackage.P + "sp"))
                        {
                            int paragraph = 0;
                            foreach (var p in shape.Element(OfficePackage.P + "txBody")?.Elements(OfficePackage.A + "p") ?? Enumerable.Empty<System.Xml.Linq.XElement>())
                                items.Add(new JObject { ["slide_number"] = i + 1, ["shape_id"] = (int?)shape.Element(OfficePackage.P + "nvSpPr")?.Element(OfficePackage.P + "cNvPr")?.Attribute("id"), ["paragraph_index"] = ++paragraph, ["text"] = OfficePackage.Text(p, OfficePackage.A) });
                        }
                }
                else throw new ToolException(ErrorCodes.UnsupportedFormat, "仅支持 .docx/.pptx。");
                if (start > items.Count && items.Count > 0) throw new ToolException(ErrorCodes.InvalidArguments, "start_item 超出范围。");
                int offset = ToolArgs.OptionalIntInRange(args, "start_char", 0, 0, 2000000), chars = 0, next = start; bool partial = false;
                var selected = new List<JObject>();
                foreach (var item in items.Skip(start - 1).Take(count))
                {
                    string text = (string)item["text"]!; int from = selected.Count == 0 ? offset : 0;
                    if (from > text.Length) throw new ToolException(ErrorCodes.InvalidArguments, "start_char 超出段落范围。");
                    int length = Math.Min(text.Length - from, Math.Min(8000, 24000 - chars));
                    if (length == 0 && text.Length > from) break;
                    if (from > 0 && from < text.Length && char.IsLowSurrogate(text[from])) throw new ToolException(ErrorCodes.InvalidArguments, "请使用上一页 next_char，避免切断字符。");
                    if (from + length < text.Length && length > 0 && char.IsHighSurrogate(text[from + length - 1])) length--;
                    var copy = (JObject)item.DeepClone(); copy["text"] = text.Substring(from, length); copy["start_char"] = from; copy["total_chars"] = text.Length;
                    partial = from + length < text.Length; copy["partial_text"] = from > 0 || partial; copy["next_char"] = partial ? from + length : 0;
                    selected.Add(copy); chars += length; if (partial) break; next++;
                }
                if (args["path"] != null) DocumentWorkspaceTool.RecordRead(_policy, context.TaskId, (string)args["path"]!, "office", start, offset, next, partial ? (int)selected.Last()["next_char"]! : 0, partial || next <= items.Count, false);
                return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["source_sha256"] = TaskOutputs.Sha256File(path), ["items"] = new JArray(selected), ["total_items"] = items.Count,
                    ["proofread_findings"] = OfficeTextProofreader.Check(selected.ToArray(), start), ["proofread_scope"] = "仅当前分页的启发式疑点，不是事实认证、完整拼写检查或页面渲染；零疑点也需通读。", ["render_verified"] = false,
                    ["has_more"] = partial || next <= items.Count, ["next_item"] = next, ["next_char"] = partial ? selected.Last()["next_char"] : 0, ["scope"] = "正文定位；partial_text=true 时为段落片段，不能直接当完整 expected_text 编辑。需按 next_item/next_char 继续。复杂原稿须人工核对排版。" }));
            }
            string fileName = ToolArgs.RequireString(args, "file_name");
            bool word = _mode.EndsWith("word", StringComparison.Ordinal); string extension = word ? ".docx" : ".pptx";
            if (!fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new ToolException(ErrorCodes.InvalidArguments, "输出文件名必须以 " + extension + " 结尾。");
            DocumentWorkspaceTool.RecordOutput(_policy, context.TaskId, fileName);
            byte[] bytes; string log = "";
            if (_mode == "write_word")
            {
                ToolArgs.RejectUnknown(args, "title", "blocks", "file_name", "draft_id", "part_index", "finalize");
                string title = OfficePackage.Field(args, "title", 100);
                var blocks = args["blocks"] as JArray ?? throw new ToolException(ErrorCodes.InvalidArguments, "blocks 必须为数组。");
                if (args["draft_id"] != null)
                {
                    var draft = WordDraftStore.Stage(_policy, context.TaskId, args, title, fileName, blocks);
                    if (!draft.Final) return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> {
                        ["status"] = "draft", ["draft_id"] = (string)args["draft_id"]!, ["file_name"] = fileName, ["title"] = title,
                        ["next_part_index"] = draft.Next, ["saved_blocks"] = draft.Blocks.Count,
                        ["notice"] = "当前仅保存内部草稿，尚未交付 Word 文件。继续下一批，全部完成后 finalize=true。" }));
                    blocks = draft.Blocks;
                }
                else if (args["part_index"] != null || args["finalize"] != null) throw new ToolException(ErrorCodes.InvalidArguments, "part_index/finalize 必须配合 draft_id 使用。");
                var budget = OfficeDocumentBudget.Load(_policy); bytes = WordAutomation.Create(title, blocks, budget.MaxDocumentChars, budget.MaxBlocks);
            }
            else if (_mode == "write_presentation")
            {
                ToolArgs.RejectUnknown(args, "slides", "file_name");
                bytes = PresentationAutomation.Create(args["slides"] as JArray ?? throw new ToolException(ErrorCodes.InvalidArguments, "slides 必须为数组。"), OfficeDocumentBudget.Load(_policy).MaxSlides);
            }
            else
            {
                ToolArgs.RejectUnknown(args, word ? new[] { "path", "artifact_name", "source_sha256", "edits", "file_name", "track_changes" } : new[] { "path", "artifact_name", "source_sha256", "edits", "file_name" });
                if ((args["path"] != null) == (args["artifact_name"] != null)) throw new ToolException(ErrorCodes.InvalidArguments, "path 与 artifact_name 必须且只能提供一个。");
                string source = args["path"] != null ? _policy.ResolveReadablePath(ToolArgs.RequireString(args, "path")) : _policy.ResolveTaskOutputPath(context.TaskId, ToolArgs.RequireString(args, "artifact_name"));
                if (!source.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new ToolException(ErrorCodes.UnsupportedFormat, "输入文件类型与工具不一致。");
                string expected = OfficePackage.Field(args, "source_sha256", 64); OfficePackage.RequireHash(source, expected);
                var edits = args["edits"] as JArray;
                if (edits == null || edits.Count < 1 || edits.Count > 50) throw new ToolException(ErrorCodes.InvalidArguments, "edits 需为 1~50 项。");
                var parts = OfficePackage.Read(source);
                if (word)
                {
                    if (args["track_changes"] != null && args["track_changes"]!.Type != JTokenType.Boolean) throw new ToolException(ErrorCodes.InvalidArguments, "track_changes 需为布尔值。");
                    WordAutomation.Edit(parts, edits, (bool?)args["track_changes"] ?? true);
                }
                else PresentationAutomation.Edit(parts, edits);
                OfficePackage.RequireHash(source, expected); bytes = OfficePackage.Pack(parts);
                log = "# Office 修改清单\n\n原件未修改；修改后的文字继承首个文字片段样式，复杂排版需人工检查。\n\n源文件 SHA-256：" + expected + "\n\n" + string.Join("\n\n", edits.Cast<JObject>().Select(e => "原文：" + OutputSanitizer.MarkdownCell((string)e["expected_text"]!) + "\n\n修改：" + OutputSanitizer.MarkdownCell((string)e["new_text"]!)));
            }
            token.ThrowIfCancellationRequested();
            string logicalName = fileName; fileName = DocumentWorkspaceTool.AvailableName(_policy, context.TaskId, fileName, bytes);
            var outputs = new List<(string, byte[], string)> { (fileName, bytes, word ? "word_document" : "presentation") };
            if (log.Length > 0) outputs.Add((fileName + ".changes.md", TaskOutputs.Utf8WithBom(log), "office_changes"));
            var artifacts = TaskOutputs.CommitFiles(_policy, context.TaskId, outputs, (path, type) => {
                if (type == "word_document" || type == "presentation") { var parts = OfficePackage.Read(path); if (word) OfficePackage.WordParagraphs(OfficePackage.Part(parts, "word/document.xml")); else OfficePackage.SlideParts(parts); }
            });
            DocumentWorkspaceTool.RecordOutput(_policy, context.TaskId, logicalName, artifacts[0].Sha256, fileName);
            return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["file_name"] = fileName, ["logical_file_name"] = logicalName, ["source_unchanged"] = true, ["validation"] = "package-and-content", ["render_verified"] = false,
                ["notice"] = "已生成可编辑的新文件，原件未修改。已做包/正文校验；未在用户电脑上渲染或执行 Office，请在交付前核对版式。" }, artifacts));
        }
    }
}
