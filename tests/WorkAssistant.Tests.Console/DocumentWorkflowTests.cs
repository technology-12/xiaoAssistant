using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;
using WorkAssistant.Tools;

namespace WorkAssistant.Tests.Console
{
    public static class DocumentWorkflowTests
    {
        static string Root() { string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wf-" + Guid.NewGuid().ToString("N").Substring(0, 8)); WorkspacePolicy.EnsureLayout(p); return p; }
        static ToolContext Context(string root, string task = "task") => new ToolContext(task, new WorkspacePolicy(root), new InMemoryAggregationStore(), CancellationToken.None) { WorkState = () => DocumentWorkspaceTool.ReadWorkState(new WorkspacePolicy(root), task) };
        static ToolResult Run(string root, JObject args, string task = "task") => new DocumentWorkspaceTool(new WorkspacePolicy(root)).ExecuteAsync(Context(root, task), args.ToString(), CancellationToken.None).GetAwaiter().GetResult();
        static JObject Args(string action, string? id = null) { var a = new JObject { ["action"] = action }; if (id != null) a["document_id"] = id; return a; }
        static ToolResult Begin(string root, string name, string task = "task") { var a = Args("begin"); a["file_name"] = name; a["title"] = "教案标题"; var r = Run(root, a, task); Test.Check(r.Ok, r.Error?.Message ?? "begin"); return r; }
        static JArray Blocks(int n, int chars = 200) => new JArray(Enumerable.Range(0, n).Select(i => new JObject { ["type"] = "paragraph", ["text"] = "教学" + i + new string('晓', chars) }));
        static JObject Append(string id, string op, JArray blocks) { var a = Args("append", id); a["operation_id"] = op; a["blocks"] = blocks; return a; }
        static string Id(ToolResult result) => result.Data["document_id"]!.ToString()!;
        static JObject Finish(string id, int revision) { var a = Args("finalize", id); a["expected_revision"] = revision; return a; }
        public static void Register()
        {
            Test.Add("Workflow_LongWord_30Batches_360Blocks_80KPlus", () => {
                string root = Root(), id = Id(Begin(root, "长教案.docx"));
                for (int i = 0; i < 30; i++) { var r = Run(root, Append(id, "section-" + i, Blocks(12, 240))); Test.Check(r.Ok && r.Artifacts.Count == 0, r.Error?.Message ?? "append"); }
                var final = Run(root, Finish(id, 30)); Test.Check(final.Ok && final.Artifacts.Count == 1, final.Error?.Message ?? "final");
                var parts = OfficePackage.Read(Path.Combine(root, final.Artifacts[0].Path)); var paragraphs = OfficePackage.WordParagraphs(OfficePackage.Part(parts, "word/document.xml"));
                Test.Equal(361, paragraphs.Count, "all paragraphs survive"); Test.Check(paragraphs.Sum(p => OfficePackage.Text(p, OfficePackage.W).Length) > 80000, "over old char cap");
                Test.Check(!Context(root).WorkState!().HasPendingWork, "all delivered");
            });
            Test.Add("Workflow_Idempotency_Patch_StaleRevision_AtomicFinal", () => {
                string root = Root(), id = Id(Begin(root, "稿件.docx")); var a = Append(id, "part", Blocks(2));
                var first = Run(root, a); string sid = first.Data["section_id"]!.ToString()!;
                Test.Check(Run(root, a).Ok, "retry"); var patch = Args("patch", id); patch["section_id"] = sid; patch["expected_revision"] = 1; patch["operation_id"] = "patch-1"; patch["blocks"] = new JArray(new JObject { ["type"] = "bullet", ["text"] = "中文条目\n第二行" }, new JObject { ["type"] = "table", ["rows"] = new JArray(new JArray("表头", "数值"), new JArray("内容", "一")) }, new JObject { ["type"] = "paragraph", ["text"] = "结束" });
                Test.Check(Run(root, patch).Ok, "patch"); Test.Check(Run(root, a).Ok, "old append retry after patch must not reappend");
                Test.Check(!Run(root, Finish(id, 1)).Ok, "stale document revision rejected");
                patch["operation_id"] = "patch-2"; Test.Check(!Run(root, patch).Ok, "stale section rejected");
                var r = Run(root, Finish(id, 2)); Test.Check(r.Ok, r.Error?.Message ?? "finish");
                Test.Equal(r.Artifacts[0].Sha256, Run(root, Finish(id, 2)).Artifacts[0].Sha256, "final idempotent");
                patch["operation_id"] = "after-final"; patch["expected_revision"] = 2; patch["blocks"] = Blocks(4);
                Test.Check(Run(root, patch).Ok && Context(root).WorkState!().HasPendingWork, "post-proofreading patch reopens current output");
                var revised = Run(root, Finish(id, 3)); Test.Check(revised.Ok && revised.Artifacts[0].Path != r.Artifacts[0].Path, "post-proofreading revision finalizes safely");
                Test.Equal(r.Artifacts[0].Sha256, TaskOutputs.Sha256File(Path.Combine(root, r.Artifacts[0].Path)), "previous final remains unchanged");
                a["blocks"] = Blocks(4); Test.Check(!Run(root, a).Ok, "id reuse with changed content rejected");
            });
            Test.Add("Workflow_PPT_45Pages_ExactNotes", () => {
                string root = Root(), id = Id(Begin(root, "课程.pptx"));
                for (int n = 0; n < 15; n++) { var a = Args("append", id); a["operation_id"] = "slides-" + n; a["slides"] = new JArray(Enumerable.Range(0, 3).Select(i => new JObject { ["title"] = "教学页" + (n * 3 + i), ["bullets"] = new JArray("教学目标", "课堂实践"), ["notes"] = "讲解要点" + n })); Test.Check(Run(root, a).Ok, "slides append"); }
                var final = Run(root, Finish(id, 15)); Test.Check(final.Ok, final.Error?.Message ?? "ppt finalize");
                Test.Equal(45, OfficePackage.SlideParts(OfficePackage.Read(Path.Combine(root, final.Artifacts[0].Path))).Count, "all slides");
            });
            Test.Add("Workflow_MultipleRequiredFiles_PendingCannotComplete", () => {
                string root = Root(); var plan = Args("plan"); plan["file_names"] = new JArray("甲.docx", "乙.docx"); Test.Check(Run(root, plan).Ok, "plan");
                string id = Id(Begin(root, "甲.docx")); Run(root, Append(id, "a", Blocks(1))); Test.Check(Run(root, Finish(id, 1)).Ok, "first output");
                Test.Check(Context(root).WorkState!().HasPendingWork, "second output outstanding");
                var events = new Sink(); var task = new TaskRecord("task", "生成文件", root);
                var controller = new AgentController(new Sequence((r, n, delta) => new ModelReply("全部完成", null, "stop")), new ToolDispatcher(BuiltInTools.Create(new WorkspacePolicy(root)), new InMemoryReceiptStore()), events, (t, ct) => Context(root, t), "mock");
                var result = controller.RunAsync(task, new AgentRunOptions { ModelRequestLimit = 5 }, CancellationToken.None).GetAwaiter().GetResult();
                Test.Equal(TaskState.Failed, result.State, "early completion blocked"); Test.Equal(ErrorCodes.NoArtifacts, result.ErrorCode, "specific failure");
            });
            Test.Add("Workflow_VisibleTruncation_Continues_UsagePreserved", () => {
                string root = Root(); var events = new Sink(); var client = new Sequence((r, n, delta) => {
                    if (n == 1) { delta?.Invoke("已完成的开头"); throw new ModelOutputTruncatedException(new ModelUsage(10, 512), "已完成的开头", "{broken"); }
                    Test.Check(r.Messages.Any(m => m.Role == "assistant" && m.Content == "已完成的开头"), "partial archived");
                    return new ModelReply("剩余内容", null, "stop", true, new ModelUsage(10, 20));
                });
                var controller = new AgentController(client, new ToolDispatcher(XpArray.Empty<IAgentTool>(), new InMemoryReceiptStore()), events, (t, ct) => Context(root, t), "mock");
                var result = controller.RunAsync(new TaskRecord("task", "回答问题", root), CancellationToken.None).GetAwaiter().GetResult();
                Test.Equal(TaskState.Completed, result.State, "continuation succeeds"); Test.Equal(532L, result.CompletionTokens, "all billed usage"); Test.Equal(1, result.ModelRetryCount, "one continuation");
            });
            Test.Add("Workflow_ConsecutiveTruncationCounterResets", () => {
                string root = Root(); var client = new Sequence((r, n, delta) => {
                    if (n % 2 == 1 && n < 7) throw new ModelOutputTruncatedException(new ModelUsage(1, 5));
                    return n < 7 ? new ModelReply(null, new[] { new ToolCall("read-" + n, "list_files", new JObject { ["directory"] = "inputs", ["extensions"] = new JArray(n == 2 ? ".txt" : n == 4 ? ".csv" : ".docx") }.ToString()) }, "tool_calls") : new ModelReply("完成", null, "stop");
                });
                var controller = new AgentController(client, new ToolDispatcher(BuiltInTools.Create(new WorkspacePolicy(root)), new InMemoryReceiptStore()), new Sink(), (t, ct) => Context(root, t), "mock");
                var result = controller.RunAsync(new TaskRecord("task", "查看文件", root), CancellationToken.None).GetAwaiter().GetResult();
                Test.Equal(TaskState.Completed, result.State, "three separated truncations recover"); Test.Equal(3, result.ModelRetryCount, "separated recoveries counted");
            });
            Test.Add("Workflow_LongLinePagination_NoUserSplitting", () => {
                string root = Root(); string text = new string('教', 70000) + "END"; File.WriteAllText(Path.Combine(root, "inputs", "long.txt"), text);
                var tool = new ReadDocumentTextTool(new WorkspacePolicy(root)); int offset = 0; string read = "";
                do { var a = new JObject { ["path"] = "inputs/long.txt", ["start_char"] = offset }; var r = tool.ExecuteAsync(Context(root), a.ToString(), CancellationToken.None).GetAwaiter().GetResult(); Test.Check(r.Ok, r.Error?.Message ?? "read"); var data = JObject.FromObject(r.Data); read += (string)data["lines"]![0]!["text"]!; offset = (int)data["next_char"]!; } while (offset > 0);
                Test.Equal(text, read, "all text recovered by offsets");
            });
            Test.Add("Workflow_TamperedSection_NotDelivered", () => {
                string root = Root(), id = Id(Begin(root, "完整性.docx")); Run(root, Append(id, "a", Blocks(1)));
                string file = Directory.GetFiles(Path.Combine(root, ".claude", "outputs", "task"), ".ds-*.json").Single(); File.AppendAllText(file, " ");
                Test.Check(!Run(root, Finish(id, 1)).Ok, "tampering rejected"); Test.Check(!File.Exists(new WorkspacePolicy(root).ResolveTaskOutputPath("task", "完整性.docx")), "no artifact");
            });
            Test.Add("Workflow_ParallelAppend_Idempotent", () => {
                string root = Root(), id = Id(Begin(root, "并发.docx")); var a = Append(id, "same-op", Blocks(2));
                var results = XpTask.WhenAll(XpTask.Run(() => Run(root, (JObject)a.DeepClone())), XpTask.Run(() => Run(root, (JObject)a.DeepClone()))).GetAwaiter().GetResult();
                Test.Check(results.All(r => r.Ok), "both acknowledged"); Test.Check(Run(root, Finish(id, 1)).Ok, "only one revision saved");
            });
            Test.Add("Workflow_ModelCapabilities_ExplicitAndGatewayPreserved", () => {
                var settings = new ModelSettings { BaseUrl = "https://api.deepseek.com", ModelName = "deepseek-flash" }; ModelCapabilities.ApplyDefaults(settings);
                Test.Equal(32768, settings.EffectiveOutputTokens, "known provider request budget"); Test.Equal(1000000, settings.ContextWindowTokens, "known context");
                settings.MaxOutputTokens = 1024; ModelCapabilities.ApplyDefaults(settings); Test.Equal(1024, settings.EffectiveOutputTokens, "explicit honored");
                settings = new ModelSettings { BaseUrl = "http://192.0.2.1", ModelName = "deepseek-flash" }; ModelCapabilities.ApplyDefaults(settings); Test.Equal(0, settings.ContextWindowTokens, "gateway not guessed");
            });
            Test.Add("Workflow_FullReadCoverage_RejectsSkippedPages", () => {
                string root = Root(); var p = new WorkspacePolicy(root); DocumentWorkspaceTool.RequireFullRead(p, "task", new[] { "inputs/a.txt" });
                DocumentWorkspaceTool.RecordRead(p, "task", "inputs/a.txt", "text", 10, 0, 20, 0, false, false);
                Test.Check(DocumentWorkspaceTool.ReadWorkState(p, "task").HasPendingWork, "last page alone is not full read");
                DocumentWorkspaceTool.RecordRead(p, "task", "inputs/a.txt", "text", 1, 0, 5, 0, true, false);
                Test.Check(DocumentWorkspaceTool.ReadWorkState(p, "task").HasPendingWork, "middle gap still pending");
                DocumentWorkspaceTool.RecordRead(p, "task", "inputs/a.txt", "text", 5, 0, 10, 0, true, false);
                Test.Check(!DocumentWorkspaceTool.ReadWorkState(p, "task").HasPendingWork, "out of order pages join only when all coverage exists");
            });
            Test.Add("Workflow_ResumeDraft_OriginalUnchanged_RejectsUnauthorized", () => {
                string root = Root(), id = Id(Begin(root, "原稿.docx", "source")); Run(root, Append(id, "one", Blocks(3)), "source");
                var sourceState = File.ReadAllText(new WorkspacePolicy(root).ResolveTaskOutputPath("source", ".document-workspace.json"));
                var args = Args("resume", id); args["file_name"] = "续写.docx"; args["source_task_id"] = "source";
                Test.Check(!Run(root, args, "next").Ok, "history unavailable denies resume");
                var ctx = new ToolContext("next", new WorkspacePolicy(root), new InMemoryAggregationStore(), CancellationToken.None, taskHistory: new Archive("source"));
                var resumed = new DocumentWorkspaceTool(new WorkspacePolicy(root)).ExecuteAsync(ctx, args.ToString(), CancellationToken.None).GetAwaiter().GetResult();
                Test.Check(resumed.Ok, resumed.Error?.Message ?? "resume");
                var final = Run(root, Finish(Id(resumed), 1), "next"); Test.Check(final.Ok && final.Artifacts.Count == 1, final.Error?.Message ?? "resume final");
                Test.Equal(sourceState, File.ReadAllText(new WorkspacePolicy(root).ResolveTaskOutputPath("source", ".document-workspace.json")), "source manifest unchanged");
            });
            Test.Add("Workflow_Reasoning_JsonIpcAndWireRoundtrip", () => {
                var message = new ChatMessage("assistant", null, new[] { new ToolCall("one", "list_files", "{}") }, reasoningContent: "synthetic reasoning");
                var round = Newtonsoft.Json.JsonConvert.DeserializeObject<ChatMessage>(Newtonsoft.Json.JsonConvert.SerializeObject(message))!;
                var json = JObject.Parse(ModelRequestJson.Serialize(new ModelRequest("model", new[] { round }, new[] { new ListFilesTool(new WorkspacePolicy(Root())).Definition })));
                Test.Equal("synthetic reasoning", (string)json["messages"]![0]!["reasoning_content"]!, "reasoning returned intact across archive and wire");
                var usage = Newtonsoft.Json.JsonConvert.DeserializeObject<ModelUsage>(Newtonsoft.Json.JsonConvert.SerializeObject(new ModelUsage(10, 100, 80)))!;
                Test.Equal(80L, usage.ReasoningTokens, "reasoning tokens preserved");
            });
            Test.Add("Workflow_NetworkInterruptedVisibleText_Continues", () => {
                string root = Root(); var model = new Sequence((r, n, delta) => { if (n == 1) { delta?.Invoke("开头"); throw new ToolException(ErrorCodes.ModelUnavailable, "synthetic disconnect"); } return new ModelReply("结尾", null, "stop"); });
                var controller = new AgentController(model, new ToolDispatcher(XpArray.Empty<IAgentTool>(), new InMemoryReceiptStore()), new Sink(), (t, ct) => Context(root, t), "mock");
                var result = controller.RunAsync(new TaskRecord("task", "回答问题", root), CancellationToken.None).GetAwaiter().GetResult();
                Test.Equal(TaskState.Completed, result.State, "network continuation succeeds"); Test.Equal("开头结尾", result.FinalText, "final answer contains retained prefix once");
            });
            Test.Add("Workflow_RevisedOutput_PreservesOriginalAndLatestEvidence", () => {
                string root = Root(); var policy = new WorkspacePolicy(root); var tool = new OfficeAutomationTool(policy, "write_word");
                var args = new JObject { ["title"] = "标题", ["file_name"] = "稿.docx", ["blocks"] = Blocks(1) };
                var old = tool.ExecuteAsync(Context(root), args.ToString(), CancellationToken.None).GetAwaiter().GetResult(); args["blocks"] = Blocks(2);
                var next = tool.ExecuteAsync(Context(root), args.ToString(), CancellationToken.None).GetAwaiter().GetResult();
                Test.Check(old.Ok && next.Ok && old.Artifacts[0].Path != next.Artifacts[0].Path, "revision uses new internal filename");
                Test.Equal(old.Artifacts[0].Sha256, TaskOutputs.Sha256File(Path.Combine(root, old.Artifacts[0].Path)), "old content untouched");
                Test.Check(!DocumentWorkspaceTool.ReadWorkState(policy, "task").HasPendingWork, "logical requirement fulfilled by latest version");
            });
            Test.Add("Workflow_LargeContextCompactsBeforeLocalHardLimit", () => {
                var request = new ModelRequest("model", new[] { ChatMessage.User(new string('a', 200000)) }, XpArray.Empty<ToolDefinition>());
                Test.Check(new ModelContextBudget(1000000, 32768).ShouldCompact(request), "large model window does not bypass local soft limit");
            });
            Test.Add("Workflow_SummaryTruncation_ReducesPrefixAndRetainsGoal", () => {
                var pinned = ChatMessage.User("原始目标"); var messages = new List<ChatMessage> { ChatMessage.System("rules"), pinned };
                for (int i = 0; i < 15; i++) messages.Add(new ChatMessage(i % 2 == 0 ? "assistant" : "user", new string('a', 20000)));
                int calls = 0, previous = 0;
                var task = new TaskRecord("task", "原始目标", Root());
                bool compacted = ConversationContextManager.CompactAsync("model", messages, XpArray.Empty<ToolDefinition>(), pinned, r => {
                    calls++; int length = r.Messages.Last().Content!.Length;
                    if (calls == 1) { previous = length; throw new ModelOutputTruncatedException(); }
                    Test.Check(length < previous, "summary retry uses a smaller prefix"); return XpTask.FromResult(new ModelReply("保留原始目标和待完成事项", null, "stop"));
                }, task, new ModelContextBudget(1000000, 32768), true).GetAwaiter().GetResult();
                Test.Check(compacted && calls == 2 && messages.Contains(pinned), "summary recovers and pins original goal");
            });
            Test.Add("Workflow_LargeToolResult_ArchivedInsteadOfAborting", () => {
                string root = Root(); var model = new Sequence((r, n, delta) => {
                    if (n == 1) return new ModelReply(null, new[] { new ToolCall("big-result", "big_result", "{}") }, "tool_calls");
                    Test.Check(r.Messages.Last().Role == "tool" && r.Messages.Last().Content!.Contains("archived") && r.Messages.Last().Content!.Length < 4096, "bounded tool reference");
                    return new ModelReply("完成", null, "stop");
                });
                var controller = new AgentController(model, new ToolDispatcher(new IAgentTool[] { new BigTool() }, new InMemoryReceiptStore()), new Sink(), (t, ct) => Context(root, t), "mock");
                var result = controller.RunAsync(new TaskRecord("task", "回答问题", root), CancellationToken.None).GetAwaiter().GetResult();
                Test.Equal(TaskState.Completed, result.State, "large result does not abort"); Test.Check(result.Steps[0].ResultJson!.Length > 150000, "full result stays in task archive");
            });
        }
        sealed class BigTool : IAgentTool
        {
            public ToolDefinition Definition { get; } = new ToolDefinition("big_result", "synthetic", JObject.Parse("{\"type\":\"object\"}"));
            public Task<ToolResult> ExecuteAsync(ToolContext context, string args, CancellationToken token) => XpTask.FromResult(ToolResult.Success(new Dictionary<string, object?> { ["text"] = new string('a', 160000) }));
        }
        sealed class Archive : ITaskHistoryReader { readonly string allowed; public Archive(string allowed) { this.allowed = allowed; } public string Read(string id, string section) { if (id != allowed) throw new ToolException(ErrorCodes.PathDenied, "denied"); return "[]"; } }
        sealed class Sequence : IStreamingModelClient
        {
            readonly Func<ModelRequest, int, Action<string>?, ModelReply> run; int n;
            public Sequence(Func<ModelRequest, int, Action<string>?, ModelReply> run) { this.run = run; }
            public Task<ModelReply> CompleteAsync(ModelRequest r, CancellationToken t) => XpTask.FromResult(run(r, ++n, null));
            public Task<ModelReply> CompleteStreamAsync(ModelRequest r, Action<string>? delta, CancellationToken t) => XpTask.FromResult(run(r, ++n, delta));
        }
        sealed class Sink : ITaskEventSink { public void Append(TaskEvent e) { } }
    }
}
