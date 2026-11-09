using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;
using WorkAssistant.Tools;

namespace WorkAssistant.Host
{
    internal static class Program
    {
        [STAThread] private static void Main(string[] args)
        {
            if (args.Length < 1 || args.Length > 2 || !Guid.TryParseExact(args[0], "N", out _)) return;
            try { RunAsync(args[0], args.Length == 2 && args[1] == "--tool").GetAwaiter().GetResult(); } catch { /* Parent owns crash reconciliation. Never log model credentials. */ }
        }
        private static async Task RunAsync(string id, bool toolMode)
        {
            NativeRuntimeBootstrap.Configure();
            string nonce = Environment.GetEnvironmentVariable("WORKASSISTANT_HOST_NONCE") ?? "";
            Environment.SetEnvironmentVariable("WORKASSISTANT_HOST_NONCE", null);
            if (nonce.Length != 64) return;
            using var pipe = new NamedPipeClientStream(".", (toolMode ? "WorkAssistant-tool-" : "WorkAssistant-") + id, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(15000).ConfigureAwait(false);
            using var channel = new HostChannel(pipe);
            await channel.SendAsync("hello", new { nonce }).ConfigureAwait(false);
            var frame = await channel.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            if (toolMode)
            {
                if ((string?)frame["type"] != "tool-run") return;
                var toolRequest = frame["payload"]!.ToObject<ToolExecutionRequest>()!;
                var policy = new WorkspacePolicy(toolRequest.Workspace, toolRequest.Snapshot);
                var executionPolicy = new AgentExecutionPolicy(toolRequest.ExecutionMode);
                var tool = BuiltInTools.Create(policy, toolRequest.ExecutionMode == AgentExecutionMode.ReadOnly).Single(t => t.Definition.Name == toolRequest.Tool);
                executionPolicy.Demand(tool.Definition);
                using var timeout = XpTask.Cts(TimeSpan.FromSeconds(tool.Definition.TimeoutSeconds));
                var context = new ToolContext(toolRequest.TaskId, policy,
                    new PersistentAggregationStore(Path.Combine(toolRequest.TaskDirectory, "aggregations")), timeout.Token,
                    new PersistentFilePlanStore(Path.Combine(toolRequest.TaskDirectory, "plans")), new DenyAllProvider(),
                    taskPlans: new PersistentTaskPlanStore(Path.Combine(toolRequest.TaskDirectory, "runtime")),
                    taskHistory: new LocalConversationArchive(new LocalTaskStore(Path.GetDirectoryName(toolRequest.TaskDirectory)), toolRequest.TaskId));
                context.ApprovedOperation = toolRequest.ApprovedOperation;
                DocumentLimits.ConfigurePdfReading(Path.Combine(toolRequest.TaskDirectory, "runtime", "pdf-ocr"),
                    message => channel.SendAsync("progress", new { message }, timeout.Token).GetAwaiter().GetResult());
                DocumentLimits.ConfigureCheckpoint((state, manifest) =>
                {
                    channel.SendAsync("checkpoint", new { state, manifest }, timeout.Token).GetAwaiter().GetResult();
                    var ack = channel.ReadAsync(timeout.Token).GetAwaiter().GetResult();
                    if ((string?)ack["type"] != "checkpoint-ack") throw new IOException("提交意图未获持久化确认。");
                });
                var result = await tool.ExecuteAsync(context, toolRequest.Arguments, timeout.Token).ConfigureAwait(false);
                await channel.SendAsync("tool-result", result).ConfigureAwait(false);
                return;
            }
            if ((string?)frame["type"] != "run") return;
            var request = frame["payload"]!.ToObject<HostRequest>()!;
            using var cts = XpTask.Cts(AgentController.TotalTimeout);
            var approval = new RemoteApproval(channel);
            var questions = new RemoteQuestions(channel);
            string stage = "接收任务";
            var execution = ExecuteAsync(request, channel, approval, questions, cts.Token, value => stage = value);
            // Only the command loop reads frames; approval continuations never race reads.
            var commands = XpTask.Run(async () =>
            {
                try
                {
                    while (!execution.IsCompleted)
                    {
                        var command = await channel.ReadAsync(cts.Token).ConfigureAwait(false);
                        if ((string?)command["type"] == "decision") approval.Decide((string?)command["payload"]?["requestId"], (bool?)command["payload"]?["approved"] == true);
                        else if ((string?)command["type"] == "answer") questions.Answer((string?)command["payload"]?["answer"]);
                        else if ((string?)command["type"] == "cancel") cts.Cancel();
                        else throw new InvalidDataException("未知宿主命令。");
                    }
                }
                catch { cts.Cancel(); }
            });
            try
            {
                var result = await execution.ConfigureAwait(false);
                if (request.PreviewPath != null) await channel.SendAsync("finished", result).ConfigureAwait(false);
                else await channel.SendAsync("finished-ref", new { taskId = result.TaskId }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await channel.SendAsync("error", HostFailureInfo.Capture(ex, stage, request.Model.ApiKey)).ConfigureAwait(false);
            }
            finally { cts.Cancel(); }
        }
        private static async Task<TaskRecord> ExecuteAsync(HostRequest request, HostChannel channel, RemoteApproval approval, RemoteQuestions questions, CancellationToken token, Action<string> reportStage)
        {
            if (request.PreviewPath != null)
            {
                reportStage("读取成果预览");
                DocumentLimits.ConfigurePdfReading(Path.Combine(request.StoreRoot, request.Task.TaskId, "runtime", "pdf-ocr"), reportStage);
                var preview = SafePreview.Read(request.Task, request.PreviewPath, request.PreviewSha256, token);
                return new TaskRecord(request.Task.TaskId, "预览", request.Task.WorkspaceRoot)
                { State = TaskState.Completed, FinalText = Newtonsoft.Json.JsonConvert.SerializeObject(preview), FinishedUtc = DateTime.UtcNow };
            }
            reportStage("打开状态库");
            var store = new LocalTaskStore(request.StoreRoot);
            var task = request.Task;
            if (!request.Fake)
            {
                var models = new WorkspaceModelCatalog(task.WorkspaceRoot);
                models.Apply(request.Model);
                if (request.SubAgentModel != null) models.Apply(request.SubAgentModel);
            }
            // Runtime policy is a host-owned snapshot, never taken from model tool arguments.
            new AgentExecutionPolicy(request.Options.ExecutionMode); TaskPermissions.Validate(request.Task.PermissionMode);
            task.ExecutionMode = request.Options.ExecutionMode;
            reportStage("准备工作目录");
            WorkspacePolicy.EnsureLayout(task.WorkspaceRoot);
            using var workspaceLock = new FileStream(Path.Combine(task.WorkspaceRoot, ".workassistant.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var taskDirectory = Path.Combine(store.Root, task.TaskId);
            reportStage("加载项目角色与命令");
            var project = new WorkspaceAgentConfiguration(task.WorkspaceRoot);
            var invocation = project.Resolve(task.UserRequest);
            var role = invocation.Role;
            task.AgentRoleId = role.Name; task.ProjectConfigHash = project.Hash;
            task.CommandName = invocation.CommandName; task.ExpandedRequest = invocation.Prompt;
            request.Options.ResolvedUserRequest = invocation.Prompt;
            role.Limit(request.Options); task.ExecutionMode = request.Options.ExecutionMode;
            var modelAlias = invocation.ModelAlias == "inherit" ? role.ModelAlias : invocation.ModelAlias;
            var rootModel = modelAlias == "worker" ? request.SubAgentModel ?? request.Model : request.Model;
            Directory.CreateDirectory(Path.Combine(taskDirectory, "configuration"));
            DurableFiles.WriteText(Path.Combine(taskDirectory, "configuration", "project.json"), project.Snapshot);
            reportStage("创建输入快照");
            var snapshotDirectory = Path.Combine(taskDirectory, "snapshot");
            task.SnapshotRoot = request.LocalToolOperation && File.Exists(Path.Combine(snapshotDirectory, "manifest.json"))
                ? snapshotDirectory : VerifiedFiles.CaptureInputs(task.WorkspaceRoot, snapshotDirectory, token);
            store.SaveTask(task);
            var policy = new WorkspacePolicy(task.WorkspaceRoot, task.SnapshotRoot);
            reportStage("打开任务存储");
            var aggregations = new PersistentAggregationStore(Path.Combine(taskDirectory, "aggregations"));
            var plans = new PersistentFilePlanStore(Path.Combine(taskDirectory, "plans"));
            var sink = new LocalTaskEventSink(store, task.TaskId);
            var taskPlans = new PersistentTaskPlanStore(Path.Combine(taskDirectory, "runtime"));
            var archive = new LocalConversationArchive(store, task.TaskId);
            sink.EventAppended += e => channel.SendAsync("event", e, token).GetAwaiter().GetResult();
            reportStage("读取输入文件目录");
            if (!request.Fake && !request.LocalToolOperation)
                WorkspaceDiscovery.AddToSnapshot(task.WorkspaceRoot, task.SnapshotRoot!, invocation.Prompt, token);
            var files = ((JArray)JObject.Parse(File.ReadAllText(Path.Combine(task.SnapshotRoot, "manifest.json")))["files"]!)
                .ToDictionary(f => (string)f["file_id"]!, f => (string)f["path"]!);
            var hashes = ((JArray)JObject.Parse(File.ReadAllText(Path.Combine(task.SnapshotRoot, "manifest.json")))["files"]!)
                .ToDictionary(f => (string)f["file_id"]!, f => (string)f["sha256"]!);
            request.Options.InputCatalog = "当前输入快照文件清单（工具的 path/paths/template_path/source 参数必须使用 file_id）：\n" +
                new JArray(files.Select(f => new JObject { ["file_id"] = f.Key, ["name"] = Path.GetFileName(f.Value), ["location"] = f.Value.StartsWith("inputs/workspace/", StringComparison.Ordinal) ? f.Value.Substring("inputs/workspace/".Length) : f.Value })).ToString() + "\n" + archive.Describe() +
                "\n" + (JObject.Parse(File.ReadAllText(Path.Combine(task.SnapshotRoot, "manifest.json")))["discovery"]?.ToString() ?? "") +
                "\n同名文件必须结合 location 区分，不明确时用 ask_user 在对话中给出文件选项。工作区发现文件作为输入副本读取，原件不变。";
            reportStage("加载所选技能");
            SkillDefinition? selectedSkill = null;
            var skills = new SkillCatalog().Load();
            foreach (var skill in project.Skills)
            {
                if (skills.Any(s => s.Id == skill.Id)) throw new ToolException(ErrorCodes.InvalidArguments, "项目技能与已有技能 ID 重复。");
                skills.Add(skill);
            }
            request.Options.SkillInstructions = "项目工作说明（不授予工具权限）：\n" + project.Instructions + "\n角色：" + role.Name + "\n" + role.Instructions;
            if (!string.IsNullOrEmpty(request.Options.SkillId))
            {
                selectedSkill = skills.FirstOrDefault(s => s.Id == request.Options.SkillId) ?? throw new ToolException(ErrorCodes.InvalidArguments, "所选技能不存在或格式无效，请重新选择。");
                task.SkillId = selectedSkill.Id; task.SkillVersion = selectedSkill.Version;
                task.SkillSelectionSource = "user";
                request.Options.SkillInstructions += "\n用户选中的技能：" + selectedSkill.Name + " v" + selectedSkill.Version + "\n" + selectedSkill.Instructions + "\n技能只提供工作方法，所有工具权限与审批规则继续生效。";
            }
            else if (!request.LocalToolOperation)
            {
                string automaticId = DefaultOfficeSkillRouter.Select(invocation.Prompt, files.Values);
                var automatic = skills.FirstOrDefault(s => s.Id == automaticId);
                if (automatic != null)
                {
                    task.SkillId = automatic.Id; task.SkillVersion = automatic.Version; task.SkillSelectionSource = "auto";
                    request.Options.SkillInstructions += "\n默认办公技能（方法参考，不改变实际工具权限）：" + automatic.Name + " v" + automatic.Version + "\n" + automatic.Instructions;
                }
            }
            reportStage("创建工具定义");
            var permissions = new WorkspaceToolPermissions(task.WorkspaceRoot);
            var inlineTools = new[] { "ask_user", "list_skills", "read_skill", "list_project_commands" };
            var metaTools = new[] { "ask_user", "list_skills", "read_skill", "update_task_plan", "read_task_history", "list_project_commands" };
            var projectTools = project.Commands.Count > 0 || project.Roles.Count > 4 ? new IAgentTool[] { new ProjectCommandsTool(project) } : XpArray.Empty<IAgentTool>();
            var tools = BuiltInTools.Create(policy, task.ExecutionMode == AgentExecutionMode.ReadOnly).Concat(new IAgentTool[] { new SkillReferenceTool(true, skills), new SkillReferenceTool(false, skills) }).Concat(projectTools)
                .Where(t => (selectedSkill == null || selectedSkill.AllowedTools.Contains(t.Definition.Name) || metaTools.Contains(t.Definition.Name)) && (!SystemPrompts.IsPlanOnly(invocation.Prompt) || !t.Definition.IsMutation)).Select(t => (IAgentTool)new FileReferenceTool(
                inlineTools.Contains(t.Definition.Name) ? t : (IAgentTool)new IsolatedTool(t.Definition, task, taskDirectory, sink.Append), files, hashes, task.SnapshotRoot!)).ToArray();
            tools = role.Apply(permissions.Apply(tools));
            var modelBudget = new ModelCallBudget();
            using var delegation = request.EnableSubAgents && (selectedSkill == null || selectedSkill.AllowedTools.Contains("delegate_tasks")) && task.ParentTaskId.Length == 0 && DelegateTasksTool.HasReadableTools(tools.Select(t => t.Definition))
                ? new DelegateTasksTool(request, store, tools.Select(t => t.Definition).ToArray(), files, hashes, modelBudget, sink.Append, project, skills) : null;
            if (delegation != null)
            {
                tools = tools.Concat(new IAgentTool[] { delegation }).ToArray();
                if (selectedSkill == null || selectedSkill.AllowedTools.Contains("import_task_artifacts")) tools = tools.Concat(new IAgentTool[] { new ImportTaskArtifactsTool(task, store, files, hashes) }).ToArray();
            }
            tools = role.Apply(permissions.Apply(tools));
            if (request.LocalToolOperation)
            {
                reportStage("执行远程内核请求的本地工具");
                return await ExecuteLocalToolAsync(request, channel, tools, sink,
                    new ToolContext(task.TaskId, policy, aggregations, token, plans, approval, questions, taskPlans, archive) { WorkState = () => DocumentWorkspaceTool.ReadWorkState(policy, task.TaskId, files) }, token).ConfigureAwait(false);
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(invocation.Prompt, "通读|全文审阅|逐段|逐页|完整阅读|全文校对|逐字")) {
                var namedInputs = files.Values.Where(f => invocation.Prompt.IndexOf(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                DocumentWorkspaceTool.RequireFullRead(policy, task.TaskId, namedInputs.Length > 0 ? namedInputs : files.Values);
            }
            reportStage("初始化模型客户端");
            IModelClient model = request.Fake ? new FakeModelClient(FakeBehavior.Auto) : ModelClientFactory.Create(rootModel);
            try
            {
                request.Options.ContextWindowTokens = rootModel.ContextWindowTokens;
                request.Options.MaxOutputTokens = rootModel.EffectiveOutputTokens;
                var controller = new AgentController(new BudgetedModelClient(model, modelBudget), new ToolDispatcher(tools, sink), sink,
                    (tid, ct) => new ToolContext(tid, policy, aggregations, ct, plans, approval, questions, taskPlans, archive) { WorkState = () => DocumentWorkspaceTool.ReadWorkState(policy, tid, files) }, request.Fake ? "demo-model" : rootModel.ModelName);
                reportStage("运行任务");
                var result = await controller.RunAsync(task, request.Options, token).ConfigureAwait(false);
                if (result.State == TaskState.Completed || result.State == TaskState.CompletedWithWarnings)
                {
                    try { ArtifactDelivery.Publish(result); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ToolException)
                    {
                        result.State = TaskState.CompletedWithWarnings;
                        result.FinalText += "\n\n文件已生成，但 output 交付副本未能全部保存；内部成果已保留，可通过文件详情查看。请检查目录权限或磁盘空间。";
                    }
                }
                store.SaveTask(result);
                return result;
            }
            finally { (model as IDisposable)?.Dispose(); }
        }

        private static async Task<TaskRecord> ExecuteLocalToolAsync(HostRequest request, HostChannel channel,
            IAgentTool[] tools, LocalTaskEventSink sink, ToolContext context, CancellationToken token)
        {
            var task = request.Task;
            var dispatcher = new ToolDispatcher(tools, sink);
            var policy = new AgentExecutionPolicy(task.ExecutionMode, !SystemPrompts.IsPlanOnly(request.Options.ResolvedUserRequest.Length == 0 ? task.UserRequest : request.Options.ResolvedUserRequest));
            var response = new LocalToolResponse { Definitions = dispatcher.AllowedDefinitions(policy),
                InputCatalog = request.Options.InputCatalog, SkillInstructions = request.Options.SkillInstructions };
            var call = request.LocalToolCall;
            if (call != null)
            {
                new AgentLoopGuard().ValidateReply(new ModelReply(null, new[] { call }, "tool_calls"));
                var definition = dispatcher.FindDefinition(call.Name);
                if (definition == null) response.Result = ToolResult.Failure(ErrorCodes.InvalidArguments, "工具未注册或不属于所选技能。");
                else if (!policy.Allows(definition)) response.Result = ToolResult.Failure(ErrorCodes.ToolPolicyDenied, "当前模式禁止该工具。");
                else
                {
                    definition = TaskPermissions.Effective(definition, call, task.PermissionMode);
                    var step = task.Steps.FirstOrDefault(s => s.CallId == call.Id);
                    if (step != null && (step.Tool != call.Name || step.ArgumentsJson != call.ArgumentsJson))
                        response.Result = ToolResult.Failure(ErrorCodes.InvalidArguments, "调用标识已经用于不同操作。");
                    else if (step != null && step.State != "Verified" && step.State != "Failed" && step.State != "Denied")
                        response.Result = ToolResult.Failure(ErrorCodes.NeedsReview, "先前调用状态不确定，禁止自动重放。");
                    else if (task.Steps.Count >= AgentController.MaxToolCalls && step == null)
                        response.Result = ToolResult.Failure(ErrorCodes.LimitExceeded, "本轮本地工具调用次数已达上限。");
                    else if (step?.State == "Denied") response.Result = ToolResult.Failure(ErrorCodes.UserApprovalDenied, "本次操作已被用户拒绝。");
                    else
                    {
                        bool fresh = step == null;
                        if (fresh)
                        {
                            step = new StepRecord { CallId = call.Id, Tool = call.Name, ArgumentsJson = call.ArgumentsJson, State = "Pending", WritesFiles = definition.ProducesArtifacts || definition.IsMutation };
                            task.Steps.Add(step); sink.SaveCheckpoint(task);
                        }
                        if (fresh && definition.RequiresApproval)
                        {
                            bool automatic = TaskPermissions.CanAutoApprove(task.PermissionMode, definition);
                            task.ApprovalRequested |= !automatic; task.State = automatic ? TaskState.Running : TaskState.WaitingForUser; sink.SaveCheckpoint(task);
                            var approvalRequest = AgentController.BuildApprovalRequest(context, call, definition);
                            bool approved = automatic || context.Approvals != null && await context.Approvals.RequestAsync(approvalRequest, token).ConfigureAwait(false);
                            if (automatic) sink.Append(new TaskEvent(Guid.NewGuid().ToString("N"), DateTime.UtcNow, task.TaskId, TaskEventKind.PermissionAutoApproved, "按本任务的完全访问权限自动批准本地工具。", call.Name));
                            context.ApprovedOperation = approved ? ApprovedToolOperation.From(approvalRequest, call) : null;
                            task.State = TaskState.Running;
                            if (!approved)
                            {
                                step!.State = "Denied";
                                response.Result = ToolResult.Failure(ErrorCodes.UserApprovalDenied, "用户拒绝了本次文件操作。");
                            }
                        }
                        if (response.Result == null)
                        {
                            step!.State = "Started"; sink.SaveCheckpoint(task);
                            sink.Append(new TaskEvent(Guid.NewGuid().ToString("N"), DateTime.UtcNow, task.TaskId, TaskEventKind.ToolCallStarted, "执行本地工具：" + call.Name, call.Name));
                            response.Result = await dispatcher.DispatchAsync(context, call, token, policy).ConfigureAwait(false);
                            step.State = response.Result.Ok ? "Verified" : "Failed";
                            step.ResultJson = ToolResultJson.Serialize(response.Result); step.UpdatedUtc = DateTime.UtcNow;
                            foreach (var artifact in response.Result.Artifacts)
                                if (task.Artifacts.All(a => a.Path != artifact.Path)) task.Artifacts.Add(artifact);
                            if (response.Result.Ok && definition.IsCalculation) task.HasCalculationEvidence = true;
                            if (response.Result.Ok && definition.IsMutation) task.HasMutationEvidence = true;
                            sink.Append(new TaskEvent(Guid.NewGuid().ToString("N"), DateTime.UtcNow, task.TaskId, TaskEventKind.ToolCallFinished,
                                response.Result.Ok ? "本地工具执行完成" : "本地工具执行失败", call.Name, response.Result.Ok));
                        }
                    }
                }
                if (response.Result?.Error?.Code == ErrorCodes.NeedsReview)
                { task.State = TaskState.NeedsReview; task.ErrorCode = ErrorCodes.NeedsReview; task.ErrorMessage = response.Result.Error.Message; task.FinishedUtc = DateTime.UtcNow; }
            }
            sink.SaveCheckpoint(task);
            await channel.SendAsync("tool-response", response, token).ConfigureAwait(false);
            return task;
        }
        private sealed class RemoteQuestions : IUserInputProvider
        {
            private readonly HostChannel _channel;
            private TaskCompletionSource<string?>? _pending;
            public RemoteQuestions(HostChannel channel) { _channel = channel; }
            public void Answer(string? answer) { _pending?.TrySetResult(answer); }
            public async Task<string?> RequestAsync(UserQuestion question, CancellationToken token)
            {
                var pending = XpTask.CompletionSource<string?>(); _pending = pending;
                using var registration = token.Register(() => pending.TrySetCanceled());
                try { await _channel.SendAsync("question", question, token).ConfigureAwait(false); return await pending.Task.ConfigureAwait(false); }
                finally { _pending = null; }
            }
        }
        private sealed class RemoteApproval : IApprovalProvider
        {
            private readonly SerializedApprovalChannel _approvals;
            public RemoteApproval(HostChannel channel) { _approvals = new SerializedApprovalChannel((request, token) => channel.SendAsync("approval", request, token)); }
            public void Decide(string? requestId, bool value) { _approvals.TryDecide(requestId, value); }
            public Task<bool> RequestAsync(ApprovalRequest request, CancellationToken token) => _approvals.RequestAsync(request, token);
        }
    }
}
