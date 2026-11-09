using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;
using WorkAssistant.Infrastructure;

namespace WorkAssistant.OpenCode
{
    /// <summary>Remote orchestration only. Office tools always run through the authenticated local Host/Worker.</summary>
    public sealed class OpenCodeTaskRunner
    {
        private readonly OpenCodeConnection _connection;
        public OpenCodeTaskRunner(OpenCodeConnection connection) { _connection = connection; }

        public async Task<TaskRecord> RunAsync(HostRequest request, IApprovalProvider approvals,
            Action<TaskEvent> onEvent, CancellationToken token, IUserInputProvider? questions = null)
        {
            using var gateway = new OpenCodeGatewayClient(_connection);
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            lifetime.CancelAfter(TimeSpan.FromMinutes(6));
            var store = new LocalTaskStore(request.StoreRoot);
            var task = request.Task;
            string? runId = null;
            void Emit(TaskEventKind kind, string text)
            {
                var e = new TaskEvent(Guid.NewGuid().ToString("N"), DateTime.UtcNow, task.TaskId, kind, text);
                if (kind != TaskEventKind.AssistantDelta) store.AppendEvent(e);
                onEvent(e);
            }
            async Task<LocalToolResponse> Local(ToolCall? call)
            {
                LocalToolResponse? response = null;
                request.Task = task; request.LocalToolOperation = true; request.LocalToolCall = call;
                task = await new TaskHostClient().RunAsync(request, approvals, onEvent, lifetime.Token, questions, value => response = value).ConfigureAwait(false);
                if (response == null) throw new ToolException(task.ErrorCode ?? ErrorCodes.WorkerUnavailable, task.ErrorMessage ?? "本地工具宿主未返回响应。");
                return response;
            }
            try
            {
                _connection.Validate();
                var health = await gateway.SendAsync("health", null, lifetime.Token).ConfigureAwait(false);
                if ((int?)health["version"] != 1 || (string?)health["upstreamVersion"] != "1.1.20")
                    throw new ToolException(ErrorCodes.RemoteKernelUnavailable, "远程内核版本不匹配。");
                task.State = TaskState.Running; task.ExecutionMode = request.Options.ExecutionMode;
                task.Kind = SystemPrompts.InferKind(task.UserRequest);
                if (SystemPrompts.IsPlanOnly(task.UserRequest) || request.Options.SkillId == "skill-creator") task.Kind = TaskKind.FileGeneration;
                if (task.ExecutionMode == AgentExecutionMode.ReadOnly)
                    task.Kind = task.Kind == TaskKind.DataAnalysis || task.Kind == TaskKind.FileGeneration && IsAnalysis(task.UserRequest) ? TaskKind.DataAnalysis : TaskKind.QuestionAnswer;
                task.Messages.Clear(); task.Messages.Add(ChatMessage.User(task.UserRequest));
                Emit(TaskEventKind.TaskStarted, "通过 OpenCode 远程内核开始任务");
                var capabilities = await Local(null).ConfigureAwait(false);
                var payload = new JObject {
                    ["version"] = 1, ["taskId"] = task.TaskId, ["sessionId"] = task.SessionId,
                    ["mode"] = task.ExecutionMode.ToString(), ["request"] = task.UserRequest,
                    ["system"] = SystemPrompts.Build(task.Kind, request.Options.IsFollowUpTurn) + "\n远程工具名统一加 xiaolocal_ 前缀，例如 aggregate_table 对应 xiaolocal_aggregate_table。\n" + capabilities.InputCatalog + "\n" + capabilities.SkillInstructions +
                        (task.ExecutionMode == AgentExecutionMode.ReadOnly ? "\n本轮只读分析，禁止导出及修改文件。" : ""),
                    ["history"] = JArray.FromObject(request.Options.PriorMessages ?? new ChatMessage[0]),
                    ["model"] = new JObject { ["baseUrl"] = request.Model.BaseUrl, ["modelName"] = request.Model.ModelName, ["apiKey"] = request.Model.ApiKey },
                    ["tools"] = new JArray(capabilities.Definitions.Select(d => new JObject { ["name"] = d.Name, ["description"] = d.Description, ["inputSchema"] = d.Parameters.DeepClone() }))
                };
                Emit(TaskEventKind.ModelRequestStarted, "OpenCode 正在编排任务");
                var created = await gateway.SendAsync("runs", payload, lifetime.Token).ConfigureAwait(false);
                runId = (string?)created["id"];
                if (!Guid.TryParseExact(runId, "N", out _)) throw new ToolException(ErrorCodes.ModelProtocolError, "远程任务标识无效。");
                store.SaveDocument("opencode-run", task.TaskId, new JObject { ["id"] = runId, ["gateway"] = _connection.BaseUrl, ["session_id"] = task.SessionId }.ToString());
                long cursor = 0;
                while (true)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    var status = await gateway.SendAsync("runs/" + runId + "?after=" + cursor, null, lifetime.Token).ConfigureAwait(false);
                    foreach (var e in (JArray?)status["events"] ?? new JArray())
                        if ((string?)e["kind"] == "delta") Emit(TaskEventKind.AssistantDelta, (string?)e["text"] ?? "");
                    cursor = (long?)status["next"] ?? cursor;
                    if (status["command"] is JObject pending)
                    {
                        string commandId = (string?)pending["id"] ?? "";
                        if (!Guid.TryParseExact(commandId, "N", out _)) throw new ToolException(ErrorCodes.ModelProtocolError, "远程工具调用标识无效。");
                        var command = await gateway.SendAsync("runs/" + runId + "/claim", new JObject { ["commandId"] = commandId }, lifetime.Token).ConfigureAwait(false);
                        var call = new ToolCall(commandId, (string)command["name"]!, command["arguments"]!.ToString(Newtonsoft.Json.Formatting.None));
                        task.Messages.Add(new ChatMessage("assistant", null, new[] { call }));
                        var local = await Local(call).ConfigureAwait(false);
                        var result = local.Result ?? ToolResult.Failure(ErrorCodes.WorkerUnavailable, "本地工具未返回结果。");
                        var serialized = ToolResultJson.Serialize(result);
                        if (serialized.Length > AgentController.MaxToolResultChars)
                            serialized = ToolResultJson.Serialize(ToolResult.Failure(ErrorCodes.LimitExceeded, "工具响应超过上下文上限，已生成成果仍保存在本地。"));
                        task.Messages.Add(new ChatMessage("tool", serialized, toolCallId: call.Id)); store.SaveTask(task);
                        // Retrying acknowledgement is safe; the local operation itself is never retried.
                        var acknowledgement = new JObject { ["commandId"] = commandId, ["result"] = JObject.Parse(serialized) };
                        for (int attempt = 0; ; attempt++)
                        {
                            try { await gateway.SendAsync("runs/" + runId + "/results", acknowledgement, lifetime.Token).ConfigureAwait(false); break; }
                            catch (System.Net.Http.HttpRequestException) when (attempt < 2) { await XpTask.Delay(300, lifetime.Token).ConfigureAwait(false); }
                        }
                        if (task.State == TaskState.NeedsReview || result.Error?.Code == ErrorCodes.UserApprovalDenied)
                            throw new ToolException(task.State == TaskState.NeedsReview ? ErrorCodes.NeedsReview : ErrorCodes.UserApprovalDenied, result.Error?.Message ?? "本地操作需要复核。");
                        continue;
                    }
                    string state = (string?)status["state"] ?? "";
                    if (state == "completed")
                    {
                        task.FinalText = (string?)status["text"] ?? "";
                        ValidateCompletion(task);
                        task.Messages.Add(new ChatMessage("assistant", task.FinalText));
                        task.State = task.Steps.Any(s => s.State == "Failed") ? TaskState.CompletedWithWarnings : TaskState.Completed;
                        Emit(TaskEventKind.TaskCompleted, "OpenCode 任务完成，本地成果已经登记"); break;
                    }
                    if (state == "failed" || state == "interrupted" || state == "cancelled")
                        throw new ToolException((string?)status["errorCode"] ?? ErrorCodes.RemoteKernelUnavailable, (string?)status["error"] ?? "远程内核已停止，未自动重放本地工具。");
                    await XpTask.Delay(300, lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                // Cancel the remote runtime even if local approval/cancellation interrupted execution.
                if (runId != null)
                {
                    try { using var timeout = XpTask.Cts(TimeSpan.FromSeconds(5)); await gateway.SendAsync("runs/" + runId + "/cancel", new JObject(), timeout.Token).ConfigureAwait(false); } catch { }
                }
                task = store.LoadTask(task.TaskId) ?? task;
                bool uncertain = task.State == TaskState.NeedsReview || task.Steps.Any(s => s.Tool == "apply_file_plan" && s.State != "Pending" && s.State != "Denied" && s.State != "Failed");
                task.State = uncertain ? TaskState.NeedsReview : token.IsCancellationRequested ? TaskState.Cancelled : TaskState.Failed;
                task.ErrorCode = uncertain ? ErrorCodes.NeedsReview : token.IsCancellationRequested ? ErrorCodes.Cancelled : ex is ToolException te ? te.Code : ErrorCodes.RemoteKernelUnavailable;
                task.ErrorMessage = uncertain ? "本地文件变更需要复核，未自动重放。" : ex is ToolException tool ? tool.Message : "远程内核连接中断；请检查服务地址和运行状态。";
                Emit(task.State == TaskState.NeedsReview ? TaskEventKind.TaskNeedsReview : task.State == TaskState.Cancelled ? TaskEventKind.TaskCancelled : TaskEventKind.TaskFailed, task.ErrorMessage);
            }
            task.FinishedUtc = DateTime.UtcNow; task.SubState = TaskSubState.None; store.SaveTask(task); return task;
        }
        private static bool IsAnalysis(string request) => System.Text.RegularExpressions.Regex.IsMatch(request, "汇总|统计|分析|合计|求和|分组|aggregate|statistics|analy[sz]e|group by", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static void ValidateCompletion(TaskRecord task)
        {
            if (task.FinalText.Trim().Length == 0) throw new ToolException(ErrorCodes.ModelProtocolError, "远程内核没有返回完整回答。");
            if (task.Kind == TaskKind.DataAnalysis && !task.HasCalculationEvidence || task.Kind == TaskKind.FileGeneration && IsAnalysis(task.UserRequest) && !task.HasCalculationEvidence)
                throw new ToolException(ErrorCodes.InvalidArguments, "尚无经过本地工具验证的计算结果。");
            if (task.Kind == TaskKind.FileModification && !task.HasMutationEvidence)
                throw new ToolException(ErrorCodes.NoArtifacts, "尚无经过审批并验证的文件变更。");
            if ((task.Kind == TaskKind.FileGeneration || task.Kind == TaskKind.FileModification) && task.Artifacts.Count == 0)
                throw new ToolException(ErrorCodes.NoArtifacts, "远程内核未通过本地工具生成有效成果。");
        }
    }
}
