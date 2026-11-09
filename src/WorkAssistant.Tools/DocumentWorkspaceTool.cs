using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>Durable task-local document workflow. Manifest commits reference immutable section revisions.</summary>
    public sealed class DocumentWorkspaceTool : AgentToolBase
    {
        readonly WorkspacePolicy policy;
        public DocumentWorkspaceTool(WorkspacePolicy policy) { this.policy = policy; }
        protected override string FallbackErrorCode => ErrorCodes.OutputWriteFailed;
        public override ToolDefinition Definition { get; } = new ToolDefinition("document_workspace",
            "本地办公文档工作台。先 plan 登记所有预期 file_names；begin 创建 docx/pptx 并返回宿主生成的 document_id；append 小批 blocks/slides；patch 按 section_id 和 expected_revision 替换；inspect 分页读取已保存章节；finalize 合成并回读验收后交付。status 查询进度；resume 从有权回查的历史任务复制草稿到本任务。已保存章节不重复生成。每个 append/patch 需稳定 operation_id，重试同ID只执行一次。", BuildSchema(), true);
        static JObject BuildSchema()
        {
            var schema = Schema(("action", "string", "plan/begin/append/patch/inspect/finalize/status/resume", true),
                ("file_names", "array", "plan：全部预期成果文件名，增量登记且不能删除既有要求", false),
                ("file_name", "string", "begin/resume：.docx 或 .pptx 文件名，同任务文件名唯一", false),
                ("title", "string", "begin：文档标题", false), ("document_id", "string", "宿主返回的文档ID", false),
                ("operation_id", "string", "append/patch 的稳定幂等键，重试保持相同", false),
                ("section_id", "string", "patch/inspect 的章节ID", false), ("expected_revision", "integer", "patch/finalize 必填，使用最近状态的 revision", false),
                ("blocks", "array", "Word 当前章节正文，每批建议≤12块/2500字；结构同 write_word", false),
                ("slides", "array", "PPT 当前章节页面，每批建议≤3页，结构同 write_presentation", false),
                ("start_item", "integer", "inspect 从1开始的章节或正文块/页面", false), ("max_items", "integer", "inspect 1~20，默认10", false),
                ("start_char", "integer", "inspect 单块 JSON 的字符偏移；超长块分页", false),
                ("source_task_id", "string", "resume：当前会话允许回查的来源任务", false), ("legacy_draft_id", "string", "可选，恢复1.0.3的write_word草稿；与document_id二选一", false));
            var p = (JObject)schema["properties"]!;
            p["action"]!["enum"] = new JArray("plan", "begin", "append", "patch", "inspect", "finalize", "status", "resume");
            p["file_names"]!["items"] = new JObject { ["type"] = "string" }; p["file_names"]!["maxItems"] = 100;
            p["blocks"]!["items"] = OfficeAutomationTool.WordBlockSchema(); p["blocks"]!["maxItems"] = 100;
            p["slides"]!["items"] = new JObject { ["type"] = "object", ["required"] = new JArray("title"), ["properties"] = new JObject {
                ["title"] = new JObject { ["type"] = "string" }, ["layout"] = new JObject { ["type"] = "string", ["enum"] = new JArray("cover", "content") },
                ["bullets"] = new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" } }, ["notes"] = new JObject { ["type"] = "string" } } };
            return schema;
        }
        const string ManifestName = ".document-workspace.json";
        const string ReadsName = ".document-read-coverage.json";
        public static void RequireFullRead(WorkspacePolicy p, string task, IEnumerable<string> files)
        {
            using var held = Lock(p, task); var reads = LoadReads(p, task);
            foreach (var file in files.Where(f => new[] { ".docx", ".pptx", ".txt", ".md", ".pdf" }.Contains(Path.GetExtension(f).ToLowerInvariant())))
            {
                if (reads[file] == null) reads[file] = new JObject { ["required"] = true, ["pages"] = new JArray() }; else reads[file]!["required"] = true;
            }
            DurableFiles.WriteText(p.ResolveTaskOutputPath(task, ReadsName), reads.ToString(Formatting.None));
        }
        static JObject LoadReads(WorkspacePolicy p, string task)
        {
            string path = p.ResolveTaskOutputPath(task, ReadsName); return File.Exists(path) ? JObject.Parse(ReadText(path)) : new JObject();
        }
        public static void RecordRead(WorkspacePolicy p, string task, string file, string mode, int startItem, int startChar, int nextItem, int nextChar, bool hasMore, bool partialDocument)
        {
            using var held = Lock(p, task); var reads = LoadReads(p, task);
            if (reads[file] is not JObject entry) { reads[file] = new JObject { ["required"] = false, ["pages"] = new JArray() }; entry = (JObject)reads[file]!; }
            var pages = (JArray)entry["pages"]!; string from = mode + ":" + startItem + ":" + startChar, to = mode + ":" + nextItem + ":" + nextChar;
            if (!pages.OfType<JObject>().Any(v => (string?)v["from"] == from && (string?)v["to"] == to))
            {
                if (pages.Count >= 4096) throw new ToolException(ErrorCodes.LimitExceeded, "输入读取分页记录达到资源上限。");
                pages.Add(new JObject { ["from"] = from, ["to"] = to, ["terminal"] = !hasMore && !partialDocument });
            }
            // Traverse all reachable page boundaries; reading pages out of order cannot hide a gap.
            var reached = new HashSet<string>(new[] { "text:1:0", "office:1:0" }, StringComparer.Ordinal); bool changed;
            do { changed = false; foreach (var page in pages.OfType<JObject>()) if (reached.Contains((string)page["from"]!) && reached.Add((string)page["to"]!)) changed = true; } while (changed);
            entry["complete"] = pages.OfType<JObject>().Any(v => reached.Contains((string)v["from"]!) && (bool)v["terminal"]!);
            entry["next"] = to;
            DurableFiles.WriteText(p.ResolveTaskOutputPath(task, ReadsName), reads.ToString(Formatting.None));
        }
        static string ReadText(string path, int limit = 16 * 1024 * 1024)
        {
            using var file = VerifiedFiles.OpenRead(path, Path.GetDirectoryName(path)!);
            if (file.Length > limit) throw new ToolException(ErrorCodes.LimitExceeded, "文档状态超过资源预算。");
            using var reader = new StreamReader(file); return reader.ReadToEnd();
        }
        static JObject Load(WorkspacePolicy p, string task)
        {
            string path = p.ResolveTaskOutputPath(task, ManifestName);
            var state = File.Exists(path) ? JObject.Parse(ReadText(path)) : new JObject { ["task_id"] = task, ["outputs"] = new JArray(), ["documents"] = new JArray() };
            if ((string?)state["task_id"] != task || state["outputs"] is not JArray || state["documents"] is not JArray)
                throw new ToolException(ErrorCodes.InvalidArguments, "文档状态归属或结构无效。");
            return state;
        }
        static void Save(WorkspacePolicy p, string task, JObject state)
        {
            string text = state.ToString(Formatting.None);
            if (Encoding.UTF8.GetByteCount(text) > 16 * 1024 * 1024) throw new ToolException(ErrorCodes.LimitExceeded, "文档状态资源预算耗尽，先前草稿保留。");
            DurableFiles.WriteText(p.ResolveTaskOutputPath(task, ManifestName), text);
        }
        static IDisposable Lock(WorkspacePolicy p, string task)
        {
            using var sha = SHA256.Create();
            var mutex = new Mutex(false, "Local\\XiaoDocuments-" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(p.RootPath.ToUpperInvariant() + "|" + task))).Replace("-", ""));
            bool held; try { held = mutex.WaitOne(10000); } catch (AbandonedMutexException) { held = true; }
            if (!held) { mutex.Dispose(); throw new ToolException(ErrorCodes.EditConflict, "文档正在保存，请稍后重试。"); }
            return new MutexLease(mutex);
        }
        sealed class MutexLease : IDisposable { readonly Mutex mutex; public MutexLease(Mutex mutex) { this.mutex = mutex; } public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); } }
        static IDisposable LockTasks(WorkspacePolicy p, string a, string b)
        {
            string first = string.CompareOrdinal(a, b) <= 0 ? a : b, second = first == a ? b : a;
            var one = Lock(p, first); try { return new PairLease(one, first == second ? null : Lock(p, second)); } catch { one.Dispose(); throw; }
        }
        sealed class PairLease : IDisposable { readonly IDisposable first; readonly IDisposable? second; public PairLease(IDisposable first, IDisposable? second) { this.first = first; this.second = second; } public void Dispose() { second?.Dispose(); first.Dispose(); } }
        static string Id(JObject args, string field)
        {
            string value = ToolArgs.RequireString(args, field);
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-Za-z0-9_-]{1,64}$")) throw new ToolException(ErrorCodes.InvalidArguments, field + " 格式无效。");
            return value;
        }
        static string SectionFile(string doc, string section, int revision) => ".ds-" + doc + "-" + section + "-" + revision + ".json";
        static JObject Document(JObject state, string id) => ((JArray)state["documents"]!).OfType<JObject>().SingleOrDefault(d => (string?)d["id"] == id)
            ?? throw new ToolException(ErrorCodes.InvalidArguments, "文档ID不存在，请先 status 或 begin。");
        static void Expect(WorkspacePolicy p, string task, JObject state, string name)
        {
            p.ResolveTaskOutputPath(task, name);
            var outputs = (JArray)state["outputs"]!;
            if (!outputs.OfType<JObject>().Any(o => string.Equals((string?)o["file_name"], name, StringComparison.OrdinalIgnoreCase)))
            {
                if (outputs.Count >= 100) throw new ToolException(ErrorCodes.LimitExceeded, "单任务预期成果超过100份，请分轮执行；草稿保留。");
                outputs.Add(new JObject { ["file_name"] = name });
            }
        }
        public static void RecordOutput(WorkspacePolicy p, string task, string name, string? hash = null, string? artifactName = null)
        {
            using var held = Lock(p, task); var state = Load(p, task); Expect(p, task, state, name);
            var item = ((JArray)state["outputs"]!).OfType<JObject>().Single(o => string.Equals((string?)o["file_name"], name, StringComparison.OrdinalIgnoreCase));
            if (hash != null) { item["sha256"] = hash; item["artifact_name"] = artifactName ?? name; } Save(p, task, state);
        }
        static bool Delivered(WorkspacePolicy p, string task, JObject output)
        {
            string path = p.ResolveTaskOutputPath(task, (string?)output["artifact_name"] ?? (string)output["file_name"]!);
            return output["sha256"] != null && File.Exists(path) && TaskOutputs.Sha256File(path) == (string?)output["sha256"];
        }
        public static TaskWorkState ReadWorkState(WorkspacePolicy p, string task, IDictionary<string, string>? fileIds = null)
        {
            using var held = Lock(p, task); var state = Load(p, task);
            var pending = ((JArray)state["outputs"]!).OfType<JObject>().Where(o => !Delivered(p, task, o)).Select(o => (string)o["file_name"]!)
                .Concat(((JArray)state["documents"]!).OfType<JObject>().Where(d => !(bool)d["finalized"]!).Select(d => (string)d["file_name"]!)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var documents = ((JArray)state["documents"]!).OfType<JObject>().Select(d => new JObject { ["document_id"] = d["id"], ["file_name"] = d["file_name"], ["revision"] = d["revision"], ["sections"] = ((JArray)d["sections"]!).Count, ["finalized"] = d["finalized"] });
            var reads = LoadReads(p, task); var incomplete = reads.Properties().Where(v => (bool?)v.Value["required"] == true && (bool?)v.Value["complete"] != true).ToArray();
            var inputReads = new JArray(reads.Properties().Select(v => new JObject { ["file"] = fileIds?.FirstOrDefault(f => f.Value == v.Name).Key ?? v.Name, ["complete"] = (bool?)v.Value["complete"] ?? false, ["required"] = (bool?)v.Value["required"] ?? false, ["last_cursor"] = v.Value["next"] }));
            string summary = new JObject { ["pending_outputs"] = new JArray(pending), ["documents"] = new JArray(documents), ["input_reads"] = inputReads }.ToString(Formatting.None);
            return new TaskWorkState { HasPendingWork = pending.Length > 0 || incomplete.Length > 0, Summary = ((JArray)state["outputs"]!).Count == 0 && reads.Count == 0 ? "" : summary };
        }
        protected override Task<ToolResult> ExecuteValidatedAsync(ToolContext context, JObject args, CancellationToken token)
        {
            ToolArgs.RejectUnknown(args, "action", "file_names", "file_name", "title", "document_id", "operation_id", "section_id", "expected_revision", "blocks", "slides", "start_item", "max_items", "start_char", "source_task_id", "legacy_draft_id");
            string action = ToolArgs.RequireString(args, "action");
            using var held = action == "resume" ? LockTasks(policy, context.TaskId, Id(args, "source_task_id")) : Lock(policy, context.TaskId); var state = Load(policy, context.TaskId);
            if (action == "status") {
                var info = ReadWorkState(policy, context.TaskId);
                return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["summary"] = info.Summary, ["has_pending_work"] = info.HasPendingWork }));
            }
            if (action == "plan")
            {
                foreach (string name in ToolArgs.OptionalStringArray(args, "file_names", 100) ?? throw new ToolException(ErrorCodes.InvalidArguments, "plan 需要 file_names。")) Expect(policy, context.TaskId, state, name);
                Save(policy, context.TaskId, state); return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["expected_outputs"] = state["outputs"] }));
            }
            if (action == "begin" || action == "resume")
            {
                string name = ToolArgs.RequireString(args, "file_name"); Expect(policy, context.TaskId, state, name);
                bool word = name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
                if (!word && !name.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)) throw new ToolException(ErrorCodes.InvalidArguments, "仅支持 docx/pptx。");
                var existing = ((JArray)state["documents"]!).OfType<JObject>().FirstOrDefault(d => string.Equals((string?)d["file_name"], name, StringComparison.OrdinalIgnoreCase));
                if (existing != null) return XpTask.FromResult(Result(existing));
                var doc = new JObject { ["id"] = Guid.NewGuid().ToString("N"), ["file_name"] = name, ["title"] = action == "begin" ? OfficePackage.Field(args, "title", 100) : "", ["revision"] = 0, ["sections"] = new JArray(), ["operations"] = new JObject(), ["finalized"] = false };
                if (action == "resume")
                {
                    string sourceTask = Id(args, "source_task_id");
                    if (sourceTask == context.TaskId || context.TaskHistory == null) throw new ToolException(ErrorCodes.PathDenied, "不能从当前任务或无权访问的历史恢复。");
                    context.TaskHistory.Read(sourceTask, "steps"); // Host enforces same conversation/workspace scope.
                    using var sourceLock = Lock(policy, sourceTask);
                    JObject source; JArray[] batches;
                    if (args["legacy_draft_id"] != null)
                    {
                        if (args["document_id"] != null || !word) throw new ToolException(ErrorCodes.InvalidArguments, "旧Word草稿仅可恢复为Word，且不能同时提供document_id。");
                        var old = JObject.Parse(ReadText(policy.ResolveTaskOutputPath(sourceTask, ".word-draft-" + Id(args, "legacy_draft_id") + ".json")));
                        if ((string?)old["task_id"] != sourceTask || old["parts"] is not JArray oldParts) throw new ToolException(ErrorCodes.PathDenied, "旧草稿归属无效。");
                        source = new JObject { ["title"] = old["title"], ["file_name"] = old["file_name"] }; batches = oldParts.Cast<JArray>().ToArray();
                        foreach (var batch in batches) WordAutomation.Validate((string)source["title"]!, batch);
                    }
                    else { source = Document(Load(policy, sourceTask), Id(args, "document_id")); batches = ((JArray)source["sections"]!).OfType<JObject>().Select(s => ReadSection(policy, sourceTask, source, s)).ToArray(); }
                    if (Path.GetExtension((string)source["file_name"]!) != Path.GetExtension(name)) throw new ToolException(ErrorCodes.InvalidArguments, "恢复时文档类型必须一致。");
                    doc["title"] = source["title"]!.DeepClone();
                    foreach (var content in batches)
                    {
                        string sid = Guid.NewGuid().ToString("N");
                        string file = SectionFile((string)doc["id"]!, sid, 1); DurableFiles.WriteText(policy.ResolveTaskOutputPath(context.TaskId, file), content.ToString(Formatting.None));
                        ((JArray)doc["sections"]!).Add(new JObject { ["id"] = sid, ["revision"] = 1, ["file"] = file, ["sha256"] = TaskOutputs.Sha256File(policy.ResolveTaskOutputPath(context.TaskId, file)), ["items"] = content.Count, ["chars"] = content.ToString(Formatting.None).Length, ["operation_id"] = "resume-" + sid });
                    }
                    doc["revision"] = 1;
                }
                ((JArray)state["documents"]!).Add(doc); Save(policy, context.TaskId, state); return XpTask.FromResult(Result(doc));
            }
            var current = Document(state, Id(args, "document_id")); var sections = (JArray)current["sections"]!;
            if (action == "inspect")
            {
                int start = ToolArgs.OptionalIntInRange(args, "start_item", 1, 1, 100000), count = ToolArgs.OptionalIntInRange(args, "max_items", 10, 1, 20);
                if (args["section_id"] == null) return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["document_id"] = current["id"], ["revision"] = current["revision"], ["sections"] = new JArray(sections.Skip(start - 1).Take(count)), ["total_items"] = sections.Count, ["next_item"] = start + Math.Min(count, Math.Max(0, sections.Count - start + 1)), ["has_more"] = start - 1 + count < sections.Count }));
                var section = sections.OfType<JObject>().SingleOrDefault(s => (string?)s["id"] == Id(args, "section_id")) ?? throw new ToolException(ErrorCodes.InvalidArguments, "章节不存在。");
                var content = ReadSection(policy, context.TaskId, current, section);
                if (start > content.Count) throw new ToolException(ErrorCodes.InvalidArguments, "start_item 超出章节范围。");
                string value = content[start - 1].ToString(Formatting.None); int offset = ToolArgs.OptionalIntInRange(args, "start_char", 0, 0, value.Length), length = Math.Min(8000, value.Length - offset);
                return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["item_json_fragment"] = value.Substring(offset, length), ["section_revision"] = section["revision"], ["document_revision"] = current["revision"], ["item"] = start, ["next_char"] = offset + length, ["has_more_chars"] = offset + length < value.Length, ["next_item"] = start + 1, ["total_items"] = content.Count }));
            }
            if (action == "append" || action == "patch")
            {
                string operation = Id(args, "operation_id"); string field = ((string)current["file_name"]!).EndsWith(".docx", StringComparison.OrdinalIgnoreCase) ? "blocks" : "slides";
                var content = args[field] as JArray ?? throw new ToolException(ErrorCodes.InvalidArguments, "需要 " + field + " 数组。");
                if (content.Count == 0 || content.Count > 100 || content.ToString(Formatting.None).Length > 50000) throw new ToolException(ErrorCodes.LimitExceeded, "单批过大，请减少章节块/页面，已保存内容保留。");
                var operations = current["operations"] as JObject; if (operations == null) { operations = new JObject(); current["operations"] = operations; }
                string identity = action + "|" + (action == "patch" ? Id(args, "section_id") : "") + "|" + Canonical(content);
                using var digest = SHA256.Create(); string operationHash = Convert.ToBase64String(digest.ComputeHash(Encoding.UTF8.GetBytes(identity)));
                if (operations[operation] is JObject prior) {
                    if ((string?)prior["hash"] != operationHash) throw new ToolException(ErrorCodes.EditConflict, "operation_id 已用于不同动作或内容。");
                    return XpTask.FromResult(Result(current, sections.OfType<JObject>().FirstOrDefault(v => (string?)v["id"] == (string?)prior["section_id"])));
                }
                if ((bool)current["finalized"]! && action == "append" && (int?)args["expected_revision"] != (int)current["revision"]!)
                    throw new ToolException(ErrorCodes.EditConflict, "交付后追加章节需提供当前文档 expected_revision；局部校订用 patch 和 section_revision，随后再次 finalize。");
                if (operations.Count >= 4096) throw new ToolException(ErrorCodes.LimitExceeded, "草稿操作记录达到资源上限，已保存内容保留。");
                var duplicate = sections.OfType<JObject>().SingleOrDefault(s => (string?)s["operation_id"] == operation);
                if (duplicate != null)
                {
                    if (!JToken.DeepEquals(ReadSection(policy, context.TaskId, current, duplicate), content) || (action == "patch" && (string?)duplicate["id"] != Id(args, "section_id"))) throw new ToolException(ErrorCodes.EditConflict, "operation_id 已用于不同内容或章节。");
                    return XpTask.FromResult(Result(current, duplicate));
                }
                if (field == "blocks") WordAutomation.Validate((string)current["title"]!, content);
                else PresentationAutomation.Create(content); // Validate just this small batch, not the complete deck.
                JObject? previous = action == "patch" ? sections.OfType<JObject>().SingleOrDefault(s => (string?)s["id"] == Id(args, "section_id")) : null;
                if (action == "patch" && (previous == null || (int?)args["expected_revision"] != (int)previous["revision"]!)) throw new ToolException(ErrorCodes.EditConflict, "章节版本变化，请 inspect 后按 section_revision 重试。");
                if (previous == null && sections.Count >= 2000) throw new ToolException(ErrorCodes.LimitExceeded, "草稿章节资源预算耗尽。");
                int chars = content.ToString(Formatting.None).Length, total = sections.OfType<JObject>().Where(s => s != previous).Sum(s => (int)s["chars"]!) + chars;
                var budget = OfficeDocumentBudget.Load(policy);
                if (total > budget.MaxDraftChars) throw new ToolException(ErrorCodes.LimitExceeded, "草稿磁盘字符预算耗尽；可调整 .claude/document-limits.json，草稿保留。");
                string sid = previous == null ? Guid.NewGuid().ToString("N") : (string)previous["id"]!; int revision = previous == null ? 1 : (int)previous["revision"]! + 1;
                string file = SectionFile((string)current["id"]!, sid, revision), path = policy.ResolveTaskOutputPath(context.TaskId, file);
                DurableFiles.WriteText(path, content.ToString(Formatting.None));
                var entry = new JObject { ["id"] = sid, ["revision"] = revision, ["file"] = file, ["sha256"] = TaskOutputs.Sha256File(path), ["items"] = content.Count, ["chars"] = chars, ["operation_id"] = operation };
                if (previous == null) sections.Add(entry); else previous.Replace(entry);
                operations[operation] = new JObject { ["hash"] = operationHash, ["section_id"] = sid };
                current["finalized"] = false;
                current["revision"] = (int)current["revision"]! + 1; Save(policy, context.TaskId, state); return XpTask.FromResult(Result(current, entry));
            }
            if (action == "finalize")
            {
                if ((int?)args["expected_revision"] != (int)current["revision"]!) throw new ToolException(ErrorCodes.EditConflict, "文档版本变化，请 status 后按最新 revision 交付。");
                string name = (string)current["file_name"]!; bool word = name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase);
                var all = new JArray(sections.OfType<JObject>().SelectMany(s => ReadSection(policy, context.TaskId, current, s)).Select(v => v.DeepClone()));
                var budget = OfficeDocumentBudget.Load(policy); byte[] bytes = word ? WordAutomation.Create((string)current["title"]!, all, budget.MaxDocumentChars, budget.MaxBlocks) : PresentationAutomation.Create(all, budget.MaxSlides);
                string actualName = AvailableName(policy, context.TaskId, name, bytes);
                var artifacts = TaskOutputs.CommitFiles(policy, context.TaskId, new[] { (actualName, bytes, word ? "word_document" : "presentation") }, (path, type) => VerifyContent(path, word, (string)current["title"]!, all));
                current["finalized"] = true;
                var delivered = ((JArray)state["outputs"]!).OfType<JObject>().Single(o => string.Equals((string?)o["file_name"], name, StringComparison.OrdinalIgnoreCase)); delivered["sha256"] = artifacts[0].Sha256; delivered["artifact_name"] = actualName;
                Save(policy, context.TaskId, state);
                return XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["document_id"] = current["id"], ["file_name"] = actualName, ["logical_file_name"] = name, ["status"] = "finalized", ["validation"] = "package-and-exact-content", ["render_verified"] = false, ["saved_sections"] = sections.Count }, artifacts));
            }
            throw new ToolException(ErrorCodes.InvalidArguments, "未知文档工作台 action。");
        }
        public static string AvailableName(WorkspacePolicy p, string task, string requested, byte[] bytes)
        {
            using var sha = SHA256.Create(); string hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            for (int n = 1; n <= 1000; n++)
            {
                string name = n == 1 ? requested : Path.GetFileNameWithoutExtension(requested) + " (" + n + ")" + Path.GetExtension(requested);
                string path = p.ResolveTaskOutputPath(task, name);
                if (!File.Exists(path) || TaskOutputs.Sha256File(path) == hash) return name;
            }
            throw new ToolException(ErrorCodes.OutputWriteFailed, "同名文档版本过多，请使用新文件名。");
        }
        static JArray ReadSection(WorkspacePolicy p, string task, JObject doc, JObject section)
        {
            string expected = SectionFile((string)doc["id"]!, (string)section["id"]!, (int)section["revision"]!);
            if ((string?)section["file"] != expected) throw new ToolException(ErrorCodes.PathDenied, "章节部件标识无效。");
            string path = p.ResolveTaskOutputPath(task, expected); string text = ReadText(path);
            using var sha = SHA256.Create(); string hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
            if (hash != (string?)section["sha256"]) throw new ToolException(ErrorCodes.EditConflict, "已保存章节校验失败，未交付不可信内容。");
            return JArray.Parse(text);
        }
        static string Canonical(JToken value) => (value is JObject obj ? new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, JToken.Parse(Canonical(p.Value))))) : value is JArray array ? new JArray(array.Select(v => JToken.Parse(Canonical(v)))) : value.DeepClone()).ToString(Formatting.None);
        static ToolResult Result(JObject doc, JObject? section = null) => ToolResult.Success(new Dictionary<string, object?> {
            ["document_id"] = doc["id"], ["file_name"] = doc["file_name"], ["revision"] = doc["revision"], ["status"] = (bool)doc["finalized"]! ? "finalized" : "draft",
            ["saved_sections"] = ((JArray)doc["sections"]!).Count, ["section_id"] = section?["id"], ["section_revision"] = section?["revision"], ["notice"] = "正文仅保存在本地；finalize 并通过内容回读后才交付。" });
        internal static void VerifyContent(string path, bool word, string title, JArray content)
        {
            var parts = OfficePackage.Read(path);
            if (word)
            {
                var expected = new List<string> { title };
                foreach (JObject block in content)
                {
                    if ((string?)block["type"] == "table") { foreach (JArray row in (JArray)block["rows"]!) expected.AddRange(row.Select(v => (string)v!)); expected.Add(""); }
                    else expected.Add(((string?)block["type"] == "bullet" ? "• " : "") + (string)block["text"]!);
                }
                var actual = OfficePackage.WordParagraphs(OfficePackage.Part(parts, "word/document.xml")).Select(p => string.Concat(p.Descendants().Where(e => e.Ancestors(OfficePackage.W + "p").FirstOrDefault() == p).Select(e => e.Name == OfficePackage.W + "t" ? e.Value : e.Name == OfficePackage.W + "br" ? "\n" : ""))).ToArray();
                if (!actual.SequenceEqual(expected.Select(v => v.Replace("\r\n", "\n").Replace('\r', '\n')))) throw new ToolException(ErrorCodes.OutputWriteFailed, "Word 回读内容与草稿不一致，未交付。");
            }
            else
            {
                var slides = OfficePackage.SlideParts(parts);
                if (slides.Count != content.Count) throw new ToolException(ErrorCodes.OutputWriteFailed, "PPT 回读页数不一致。");
                for (int i = 0; i < slides.Count; i++)
                {
                    var texts = OfficePackage.Part(parts, slides[i]).Descendants(OfficePackage.A + "t").Select(t => t.Value).ToArray();
                    var slide = (JObject)content[i]; bool cover = (string?)slide["layout"] == "cover";
                    if (!texts.SequenceEqual(new[] { (string)slide["title"]! }.Concat((slide["bullets"] as JArray ?? new JArray()).Select(v => (cover ? "" : "• ") + (string)v!).DefaultIfEmpty("")).Concat(new[] { (i + 1) + " / " + content.Count }))) throw new ToolException(ErrorCodes.OutputWriteFailed, "PPT 回读正文与草稿不一致。");
                    var notes = OfficePackage.Part(parts, "ppt/notesSlides/notesSlide" + (i + 1) + ".xml");
                    if (string.Concat(notes.Descendants(OfficePackage.A + "t").Select(t => t.Value)) != ((string?)slide["notes"] ?? "")) throw new ToolException(ErrorCodes.OutputWriteFailed, "PPT 讲稿回读不一致。");
                }
            }
        }
    }
    public sealed class OfficeDocumentBudget
    {
        public int MaxDocumentChars { get; private set; } = 1000000;
        public int MaxBlocks { get; private set; } = 10000;
        public int MaxSlides { get; private set; } = 200;
        public int MaxDraftChars { get; private set; } = 8000000;
        public static OfficeDocumentBudget Load(WorkspacePolicy policy)
        {
            var result = new OfficeDocumentBudget(); string path = Path.Combine(policy.RootPath, ".claude", "document-limits.json");
            if (!File.Exists(path)) return result;
            using var file = VerifiedFiles.OpenRead(path, policy.RootPath); if (file.Length > 16384) throw new ToolException(ErrorCodes.InvalidArguments, "document-limits.json 过大。");
            using var reader = new StreamReader(file); var args = ToolArgs.Parse(reader.ReadToEnd());
            ToolArgs.RejectUnknown(args, "max_document_chars", "max_blocks", "max_slides", "max_draft_chars");
            result.MaxDocumentChars = ToolArgs.OptionalIntInRange(args, "max_document_chars", result.MaxDocumentChars, 10000, 2000000);
            result.MaxBlocks = ToolArgs.OptionalIntInRange(args, "max_blocks", result.MaxBlocks, 200, 20000);
            result.MaxSlides = ToolArgs.OptionalIntInRange(args, "max_slides", result.MaxSlides, 40, 300);
            result.MaxDraftChars = ToolArgs.OptionalIntInRange(args, "max_draft_chars", result.MaxDraftChars, 100000, 16000000);
            return result;
        }
    }
}
