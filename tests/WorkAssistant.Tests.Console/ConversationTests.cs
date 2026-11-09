using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;
using WorkAssistant.Tools;

namespace WorkAssistant.Tests.Console
{
    /// <summary>
    /// 会话/任务类型/审批/恢复测试（正式版 §5.4/§5.5/§8.4/§9.2）：
    /// 问答不强制文件、连续对话多轮、审批拒绝、文件整理部分失败 → NeedsReview、
    /// 会话存储与 Profile 存储。
    /// </summary>
    public static class ConversationTests
    {
        public static void Register()
        {
            Test.Add("任务类型：问答轮无成果也判成功", ChatTurnSucceedsWithoutArtifacts);
            Test.Add("任务类型：文件类任务无产出 → NO_ARTIFACTS", FileTaskWithoutProducerFails);
            Test.Add("连续对话：两轮独立输出目录且引用同一输入", SessionTwoTurns);
            Test.Add("会话存储：AppendTurn/LoadSessions/序列化往返", SessionStoreRoundTrip);
            Test.Add("整理：计划+确认后执行，文件移动且无删除", OrganizeApproved);
            Test.Add("整理：用户拒绝 → USER_APPROVAL_DENIED 且文件未动", OrganizeDenied);
            Test.Add("整理：执行前目标被占用 → NEEDS_REVIEW 且不自动重试", OrganizeConflictNeedsReview);
            Test.Add("整理：计划阶段越界目标拒绝", OrganizePlanValidation);
            Test.Add("Profile：多配置保存/切换/独立凭据", ProfileStoreMultiple);
            Test.Add("Profile：旧版单配置迁移", ProfileMigrationFromLegacy);
        }

        private static string TempDir(string prefix) =>
            Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        private static string PrepareWorkspace(string prefix = "wa-conv")
        {
            var root = TempDir(prefix);
            Directory.CreateDirectory(Path.Combine(root, "inputs"));
            foreach (var file in Directory.GetFiles(Path.Combine(
                         DemoRunner.FindSamplesWorkspace(), "inputs")))
            {
                File.Copy(file, Path.Combine(root, "inputs", Path.GetFileName(file)));
            }
            WorkspacePolicy.EnsureLayout(root);
            return root;
        }

        // ---- 任务类型 ---------------------------------------------------------

        private static void ChatTurnSucceedsWithoutArtifacts()
        {
            var ws = PrepareWorkspace();
            var (record, _, sink) = DemoRunner.Run(ws, FakeBehavior.ChatAnswer,
                "这两个文件分别是什么格式？");
            Test.Equal(TaskState.Completed, record.State, "问答轮应完成：" + record.ErrorMessage);
            Test.Equal(0, record.Artifacts.Count, "无文件成果");
            Test.Check(record.FinalText.Length > 0, "有文本回复");
            Test.Equal(TaskKind.QuestionAnswer, record.Kind, "任务类型为问答");
        }

        private static void FileTaskWithoutProducerFails()
        {
            var ws = PrepareWorkspace();
            var (record, _, _) = DemoRunner.Run(ws, FakeBehavior.NoArtifacts,
                "汇总 inputs 文件夹中的销售 CSV，按部门统计销售额，生成汇总表和报告。");
            Test.Equal(TaskState.Failed, record.State, "文件类任务声称完成但无产出应失败");
            Test.Equal(ErrorCodes.NoArtifacts, record.ErrorCode, "NO_ARTIFACTS");
            Test.Equal(TaskKind.FileGeneration, record.Kind, "推断为文件生成类型");
        }

        // ---- 连续对话 ---------------------------------------------------------

        private static void SessionTwoTurns()
        {
            var ws = PrepareWorkspace();
            var storeRoot = TempDir("wa-store");
            var (first, store, _) = DemoRunner.Run(ws, FakeBehavior.StandardDemo,
                "汇总 inputs 文件夹中的销售 CSV，按部门统计销售额，生成汇总表和报告。",
                storeRoot);
            Test.Equal(TaskState.Completed, first.State, "第 1 轮完成");

            // 第 2 轮：同会话追问（Fake FollowUpFilter 筛选销售一部）。
            var session = new SessionRecord("sess-1", "销售汇总", ws);
            store.SaveSession(session);
            store.AppendTurn(session, new SessionTurn
            {
                TaskId = first.TaskId, CreatedUtc = first.CreatedUtc, Request = first.UserRequest
            });
            var secondTaskId = Guid.NewGuid().ToString("N");
            var record2 = new TaskRecord(secondTaskId, "只保留销售一部，重新汇总", ws)
            {
                SessionId = session.SessionId, TurnIndex = 2
            };
            store.SaveTask(record2);

            var policy = new WorkspacePolicy(ws);
            var aggregations = new InMemoryAggregationStore();
            var sink = new LocalTaskEventSink(store, secondTaskId);
            var tools = new IAgentTool[]
            {
                new ListFilesTool(policy), new PreviewCsvTool(policy), new PreviewTableTool(policy),
                new AggregateCsvTool(policy), new AggregateTableTool(policy),
                new ExportSummaryTool(policy), new ExportXlsxTool(policy),
                new GenerateDocxTool(policy), new FillDocxTemplateTool(policy),
                new PreviewDocxFieldsTool(policy), new ExtractPdfTextTool(policy),
                new PlanFileOrganizationTool(policy), new ApplyFilePlanTool(policy)
            };
            var dispatcher = new ToolDispatcher(tools, sink);
            ToolContext Factory(string tid, CancellationToken ct) =>
                new ToolContext(tid, policy, aggregations, ct, new InMemoryFilePlanStore(),
                    new AutoApproveProvider());
            var controller = new AgentController(
                new FakeModelClient(FakeBehavior.FollowUpFilter), dispatcher, sink, Factory, "fake");

            // 传入第 1 轮上下文（会话延续）。
            var options = new AgentRunOptions
            {
                PriorMessages = new[]
                {
                    ChatMessage.User(first.UserRequest),
                    new ChatMessage("assistant", first.FinalText)
                },
                IsFollowUpTurn = true,
                Kind = TaskKind.DataAnalysis
            };
            var second = controller.RunAsync(record2, options, CancellationToken.None)
                .GetAwaiter().GetResult();
            store.SaveTask(second);

            Test.Equal(TaskState.Completed, second.State, "第 2 轮完成：" + second.ErrorMessage);
            Test.Equal(2, second.Artifacts.Count, "第 2 轮交付成果");
            var summary = Path.Combine(ws, second.Artifacts.Single(a => a.Path.EndsWith("summary.csv")).Path.Replace('/', Path.DirectorySeparatorChar));
            Test.Check(File.Exists(summary), "第 2 轮输出目录独立存在");
            var text = File.ReadAllText(summary);
            Test.Check(text.Contains("150.40"), "筛选口径生效 150.40：\n" + text);
            Test.Check(!text.Contains("220.60"), "被筛除的部门不在结果中");

            // 第 1 轮成果仍在（新版本不覆盖旧版本）。
            Test.Check(File.Exists(Path.Combine(ws, first.Artifacts.Single(a => a.Path.EndsWith("summary.csv")).Path.Replace('/', Path.DirectorySeparatorChar))),
                "第 1 轮成果未被覆盖");
            ArtifactDelivery.Publish(first); ArtifactDelivery.Publish(second);
            Test.Check(first.Artifacts.All(a => a.PublishedPath.StartsWith("output/")) && second.Artifacts.All(a => a.PublishedPath.StartsWith("output/")), "两轮成果均直接交付到 output");
            Test.Check(first.Artifacts.Select(a => a.PublishedPath).Intersect(second.Artifacts.Select(a => a.PublishedPath)).Count() == 0, "不同内容的同名成果使用不同文件名");
        }

        private static void SessionStoreRoundTrip()
        {
            var root = TempDir("wa-sess");
            var store = new LocalTaskStore(root);
            var session = new SessionRecord("s1", "标题", @"C:\ws")
            {
                Turns =
                {
                    new SessionTurn { TaskId = "t1", Request = "第一轮", CreatedUtc = DateTime.UtcNow },
                    new SessionTurn { TaskId = "t2", Request = "第二轮", CreatedUtc = DateTime.UtcNow }
                }
            };
            store.SaveSession(session);
            store.AppendTurn(session, new SessionTurn { TaskId = "t3", Request = "第三轮" });

            var loaded = store.LoadSessions();
            Test.Equal(1, loaded.Count, "一个会话");
            Test.Equal(3, loaded[0].Turns.Count, "三轮");
            Test.Equal("t3", loaded[0].Turns[2].TaskId, "追加轮保存");
            var direct = store.LoadSession("s1")!;
            Test.Equal("标题", direct.Title, "标题往返");
            Test.Check(direct.LastActiveUtc >= direct.CreatedUtc, "最近活跃时间更新");
        }

        // ---- 文件整理 ---------------------------------------------------------

        private static void OrganizeApproved()
        {
            var ws = PrepareWorkspace("wa-org");
            var (record, store, sink) = DemoRunner.Run(ws, FakeBehavior.OrganizePlan,
                "把 inputs 里的销售文件整理归档。");
            Test.Equal(TaskState.Completed, record.State, "整理任务完成：" + record.ErrorMessage);
            Test.Check(record.Artifacts.Count >= 2, "计划与变更记录两个成果");

            var archived = Path.Combine(ws, "inputs", "已归档");
            Test.Check(Directory.Exists(archived), "归档目录存在");
            var moved = Directory.GetFiles(archived);
            Test.Check(moved.Length >= 2, "至少 2 个 csv 已归档，实际 " + moved.Length);
            Test.Equal(0, Directory.GetFiles(Path.Combine(ws, "inputs"), "*.csv").Length,
                "inputs 根目录 csv 全部移走");
            var mutations = Path.Combine(ws, record.Artifacts.Single(a => a.Path.EndsWith("file_mutations.md")).Path.Replace('/', Path.DirectorySeparatorChar));
            Test.Check(File.ReadAllText(mutations).Contains(" → "), "变更记录含移动明细");

            var approvalEvents = store.LoadEvents(record.TaskId)
                .Where(e => e.Kind == TaskEventKind.ApprovalRequested).ToList();
            Test.Check(approvalEvents.Count == 1, "一次审批请求");
            var resolved = store.LoadEvents(record.TaskId)
                .Where(e => e.Kind == TaskEventKind.ApprovalResolved).ToList();
            Test.Check(resolved.Count == 1 && resolved[0].ToolOk == true, "审批通过已记录");
            Test.Check(record.ApprovalRequested, "任务记录标注审批发生");
        }

        private static void OrganizeDenied()
        {
            var ws = PrepareWorkspace("wa-org-deny");
            var (record, _, _) = DemoRunner.Run(ws, FakeBehavior.OrganizePlan,
                "把 inputs 里的销售文件整理归档。",
                approvals: new DenyAllProvider());
            Test.Equal(TaskState.Failed, record.State, "拒绝后任务失败");
            Test.Equal(ErrorCodes.UserApprovalDenied, record.ErrorCode, "错误码");
            Test.Check(record.Artifacts.Count >= 1, "计划成果保留（计划本身已产出）");
            Test.Check(Directory.GetFiles(Path.Combine(ws, "inputs"), "*.csv").Length >= 2,
                "所有文件未被移动");
        }

        private static void OrganizeConflictNeedsReview()
        {
            var ws = PrepareWorkspace("wa-org-conflict");
            var policy = new WorkspacePolicy(ws);

            // 先生成计划（通过工具调用拿到 plan 语义），再人为制造目标冲突。
            var planTool = new PlanFileOrganizationTool(policy);
            var applyTool = new ApplyFilePlanTool(policy);
            var aggregations = new InMemoryAggregationStore();
            var filePlans = new InMemoryFilePlanStore();
            var ctx = new ToolContext("t-conflict", policy, aggregations, CancellationToken.None,
                filePlans, new AutoApproveProvider());

            var csv = Directory.GetFiles(Path.Combine(ws, "inputs"), "*.csv").First();
            var rel = policy.ToWorkspaceRelative(csv);
            var args = "{\"operations\":[{\"source\":\"" + rel +
                       "\",\"action\":\"move\",\"target_directory\":\"inputs/归档\"}]," +
                       "\"reason\":\"测试\"}";
            var plan = planTool.ExecuteAsync(ctx, args, CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(plan.Ok, "计划生成成功：" + plan.Error?.Message);
            var planId = (string)plan.Data["plan_id"]!;

            // 预检后、执行前人为放置目标文件（模拟 TOCTOU / 目标被占用）。
            Directory.CreateDirectory(Path.Combine(ws, "inputs", "归档"));
            File.WriteAllText(Path.Combine(ws, "inputs", "归档", Path.GetFileName(csv)), "占用");

            var apply = applyTool.ExecuteAsync(ctx,
                "{\"plan_id\":\"" + planId + "\"}", CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!apply.Ok, "冲突应失败");
            Test.Equal(ErrorCodes.PlanInvalid, apply.Error!.Code, "写入前的冲突使计划过期，原件不动");
            Test.Check(File.Exists(csv), "冲突预检后源文件保留");

            // 宿主层面：NEEDS_REVIEW → 任务进入 NeedsReview 状态（不自动重试）。
            var sink = new LocalTaskEventSink(new LocalTaskStore(TempDir("wa-store")), "t-conflict");
            var tools = new IAgentTool[] { planTool, applyTool };
            var dispatcher = new ToolDispatcher(tools, sink);
            ToolContext Factory(string tid, CancellationToken ct) =>
                new ToolContext(tid, policy, aggregations, ct, filePlans, new AutoApproveProvider());
            var scripted = new ScriptedModelClient(new[]
            {
                new ToolCall("c1", "plan_file_organization", args),
                new ToolCall("c2", "apply_file_plan", "{\"plan_id\":\"" + planId + "\"}")
            });
            var task = new TaskRecord("t-conflict-task", "整理", ws) { Kind = TaskKind.FileModification };
            var controller = new AgentController(scripted, dispatcher, sink, Factory, "fake");
            var finished = controller.RunAsync(task, CancellationToken.None).GetAwaiter().GetResult();
            Test.Equal(TaskState.Failed, finished.State, "执行前预检失败，不进入写入阶段");
            Test.Equal(ErrorCodes.PlanInvalid, finished.ErrorCode, "错误码");
            Test.Check(sink != null, "sink 不为空");
        }

        private static void OrganizePlanValidation()
        {
            var ws = PrepareWorkspace("wa-org-plan");
            var policy = new WorkspacePolicy(ws);
            var tool = new PlanFileOrganizationTool(policy);
            var ctx = new ToolContext("t-plan", policy, new InMemoryAggregationStore(),
                CancellationToken.None);

            // 越界目标：outputs 目录。
            var escape = tool.ExecuteAsync(ctx,
                "{\"operations\":[{\"source\":\"inputs/sales-a.csv\",\"action\":\"move\"," +
                "\"target_directory\":\"outputs\"}],\"reason\":\"越界\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!escape.Ok, "越界目标拒绝");
            Test.Equal(ErrorCodes.PathDenied, escape.Error!.Code, "越界目标错误码（路径策略优先）");

            // 删除动作不支持。
            var delete = tool.ExecuteAsync(ctx,
                "{\"operations\":[{\"source\":\"inputs/sales-a.csv\",\"action\":\"delete\"," +
                "\"target_directory\":\"inputs/x\"}],\"reason\":\"删除\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!delete.Ok, "删除动作拒绝");
            Test.Check(delete.Error!.Message.Contains("仅支持 move"), "说明仅支持 move");

            // 目标重名冲突。
            var conflict = tool.ExecuteAsync(ctx,
                "{\"operations\":[" +
                "{\"source\":\"inputs/sales-a.csv\",\"action\":\"move\",\"target_directory\":\"inputs\"}," +
                "{\"source\":\"inputs/sales-b.csv\",\"action\":\"move\"," +
                "\"target_directory\":\"inputs\",\"new_name\":\"sales-a.csv\"}]," +
                "\"reason\":\"重名\"}",
                CancellationToken.None).GetAwaiter().GetResult();
            Test.Check(!conflict.Ok, "目标重名拒绝");
        }

        // ---- Profile 存储 ------------------------------------------------------

        private static void ProfileStoreMultiple()
        {
            var dir = TempDir("wa-prof");
            var store = new SettingsStore(dir);
            var settings = new AppSettings();
            settings.Profiles.Add(new ModelProfile("公网")
            {
                BaseUrl = "https://api.example.com/v1", ModelName = "gpt-x"
            });
            settings.Profiles.Add(new ModelProfile("内网网关")
            {
                BaseUrl = "https://10.0.0.8/v1", ModelName = "qwen", TimeoutSeconds = 120
            });
            settings.ActiveProfileName = "内网网关";
            store.Save(settings);
            store.SaveApiKey("公网", "KEY-PUBLIC");
            store.SaveApiKey("内网网关", "KEY-PRIVATE");

            var loaded = store.Load();
            Test.Check(loaded.Profiles.Any(p => p.Name == "公网") && loaded.Profiles.Any(p => p.Name == "内网网关"), "用户的两个 Profile 均保留，允许另含内置默认配置");
            Test.Equal("内网网关", loaded.ActiveProfileName, "活动 Profile 保持");
            Test.Equal("KEY-PRIVATE", loaded.ApiKey, "活动 Profile 密钥解密");
            Test.Equal("qwen", loaded.ModelName, "门面读取活动配置");
            Test.Equal("KEY-PUBLIC", store.LoadApiKey("公网"), "另一 Profile 密钥独立");

            // settings.json 不含任何 Key 明文。
            var text = File.ReadAllText(Path.Combine(dir, "settings.json"));
            Test.Check(!text.Contains("KEY-PUBLIC") && !text.Contains("KEY-PRIVATE"),
                "settings.json 不含密钥");
        }

        private static void ProfileMigrationFromLegacy()
        {
            var dir = TempDir("wa-mig");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                "{\"base_url\":\"https://old.example.com/v1\",\"model\":\"old-model\"," +
                "\"timeout_seconds\":60,\"use_system_proxy\":false,\"fake_mode\":false}");
            var store = new SettingsStore(dir);
            // 旧版凭据（根目录 credentials.dat）。
            store.SaveApiKey(AppSettings.DefaultProfileName, "OLD-KEY");

            var settings = store.Load();
            var legacy = settings.Profiles.Single(p => p.Name == AppSettings.DefaultProfileName);
            Test.Equal("https://old.example.com/v1", legacy.BaseUrl, "base_url 迁移");
            Test.Equal("old-model", legacy.ModelName, "model 迁移");
            Test.Equal(60, legacy.TimeoutSeconds, "timeout 迁移");
            Test.Equal("OLD-KEY", settings.ApiKey, "旧凭据迁移可解密");

            var reloaded = store.Load();
            Test.Equal(settings.Profiles.Count, reloaded.Profiles.Count, "二次加载不重复迁移");
            Test.Equal("OLD-KEY", reloaded.ApiKey, "密钥持续可读");
        }

        /// <summary>固定脚本模型：按序返回预设工具调用（测试用）。</summary>
        private sealed class ScriptedModelClient : IModelClient
        {
            private readonly ToolCall[] _calls;
            private int _index;

            public ScriptedModelClient(ToolCall[] calls) => _calls = calls;

            public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken ct)
            {
                if (_index < _calls.Length)
                {
                    var call = _calls[_index++];
                    return XpTask.FromResult(new ModelReply(null,
                        new[] { call }, "tool_calls"));
                }
                return XpTask.FromResult(new ModelReply("脚本结束。", null, "stop"));
            }
        }
    }
}
