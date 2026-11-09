using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    /// <summary>
    /// 本地任务存储（设计 §11）：
    /// SQLite 保存任务、消息、事件、回执；任务目录保存快照与中间结果。
    /// 消息属于本地用户数据；导出的支持诊断不含消息或文件内容。磁盘时间统一 UTC。
    /// </summary>
    public sealed class LocalTaskStore
    {
        private readonly string _root;
        private readonly SqliteStateStore _db;

        public LocalTaskStore(string? root = null)
        {
            _root = root ?? Path.Combine(ApplicationDataPaths.Root, "tasks");
            Directory.CreateDirectory(_root);
            _db = new SqliteStateStore(_root);
        }

        public string Root => _root;
        public void Backup(string destination) => _db.Backup(destination);
        public void Restore(string source) => _db.Restore(source);
        public int Cleanup(int retentionDays, int quotaMiB)
        {
            long limit = Math.Max(100, quotaMiB) * 1024L * 1024;
            long Size(string dir)
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return 0;
                return Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length) + Directory.GetDirectories(dir).Sum(Size);
            }
            long bytes = Size(_root); int count = 0;
            foreach (var task in LoadTasks().Where(t => t.State == TaskState.Completed || t.State == TaskState.CompletedWithWarnings || t.State == TaskState.Cancelled || t.State == TaskState.Failed).OrderBy(t => t.CreatedUtc))
            {
                if (task.CreatedUtc > DateTime.UtcNow.AddDays(-Math.Max(1, retentionDays)) && bytes <= limit) continue;
                string directory = TaskDir(task.TaskId); long size = Directory.Exists(directory) ? Size(directory) : 0;
                DeleteTask(task.TaskId); bytes -= size; count++;
            }
            return count;
        }
        public void SaveDocument(string kind, string id, string json) => _db.Put(kind, id, json);
        public string? LoadDocument(string kind, string id) => _db.Get(kind, id);
        public List<string> LoadDocuments(string kind) => _db.List(kind);
        public List<ProjectRecord> LoadProjects() => _db.List("project").Select(JsonConvert.DeserializeObject<ProjectRecord>).Where(p => p != null).Cast<ProjectRecord>().OrderByDescending(p => p.UpdatedUtc).ToList();
        public void SaveProject(ProjectRecord project)
        {
            if (!Guid.TryParseExact(project.ProjectId, "N", out _) || string.IsNullOrWhiteSpace(project.Name) || project.Name.Length > 80 ||
                project.Description.Length > 1500 || !Path.IsPathRooted(project.WorkspaceRoot)) throw new InvalidOperationException("项目名称或工作目录无效。");
            project.Name = project.Name.Trim(); project.WorkspaceRoot = Path.GetFullPath(project.WorkspaceRoot);
            project.UpdatedUtc = DateTime.UtcNow;
            _db.Put("project", project.ProjectId, JsonConvert.SerializeObject(project));
        }
        // Deliberately no filesystem operations: retain all tasks, snapshots, inputs and artifacts.
        public void DeleteProject(ProjectRecord project) => _db.DeleteProjectMetadata(project.ProjectId);
        public void DeleteConversation(string sessionId) => _db.DeleteConversationMetadata(sessionId);
        public void DeleteTask(string id)
        {
            var task = LoadTask(id);
            if (task == null) return;
            string directory = Path.GetFullPath(TaskDir(id));
            string root = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!directory.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("任务目录越界。");
            _db.DeleteTask(id);
            foreach (var session in LoadSessions())
            { if (session.Turns.RemoveAll(t => t.TaskId == id) > 0) SaveSession(session); }
            if (Directory.Exists(directory)) DeleteTreeWithoutLinks(directory);
        }
        private static void DeleteTreeWithoutLinks(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("拒绝删除目录联接。");
            foreach (var child in Directory.GetDirectories(directory)) DeleteTreeWithoutLinks(child);
            foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }

        private string TaskDir(string taskId)
        {
            if (taskId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException("非法 task_id");
            }
            var full = Path.GetFullPath(Path.Combine(_root, taskId));
            if (!full.StartsWith(Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("任务目录越界。");
            return full;
        }

        // ---- task.json ------------------------------------------------------

        public void SaveTask(TaskRecord task)
        {
            Directory.CreateDirectory(TaskDir(task.TaskId));
            _db.Put("task", task.TaskId, SerializeTask(task));
        }

        public List<TaskRecord> LoadTasks()
        {
            return _db.List("task").Select(DeserializeTask).OrderByDescending(t => t.CreatedUtc).ToList();
        }
        public TaskState? LoadTaskState(string taskId)
        {
            string? json = _db.Get("task", taskId);
            if (json == null) return null;
            using var text = new StringReader(json);
            using var reader = new JsonTextReader(text);
            while (reader.Read())
                if (reader.TokenType == JsonToken.PropertyName && (string?)reader.Value == "state")
                {
                    if (!reader.Read()) return null;
                    return Enum.TryParse(reader.Value?.ToString(), out TaskState state) ? state : (TaskState?)null;
                }
            return null;
        }
        public TaskRecord? LoadTask(string taskId)
        {
            string? json = _db.Get("task", taskId);
            return json == null ? null : DeserializeTask(json);
        }

        /// <summary>启动时将遗留的 Running/WaitingForUser 任务标记为 Interrupted，不自动重新执行。</summary>
        public void MarkInterruptedTasks()
        {
            var tasks = LoadTasks().ToDictionary(t => t.TaskId, StringComparer.Ordinal);
            var active = tasks.Values.Where(t => TaskInterruption.IsActive(t) && (t.State != TaskState.Queued || t.ParentTaskId.Length > 0)).ToArray();
            foreach (var task in active) TaskInterruption.Mark(task, false);
            for (int depth = 0; depth < active.Length; depth++)
            {
                bool changed = false;
                foreach (var task in active.Where(t => t.State != TaskState.NeedsReview))
                    if (task.ChildTaskIds.Any(id => tasks.TryGetValue(id, out var child) && child.ParentTaskId == task.TaskId && child.SessionId == task.SessionId && child.State == TaskState.NeedsReview))
                    { TaskInterruption.Mark(task, false, childNeedsReview: true); changed = true; }
                if (!changed) break;
            }
            foreach (var task in active) SaveTask(task);
        }

        public TaskRecord InterruptTaskTree(string taskId, bool cancelled, string? failureCode = null)
        {
            var tasks = LoadTasks().ToDictionary(t => t.TaskId, StringComparer.Ordinal);
            if (!tasks.TryGetValue(taskId, out var root)) throw new InvalidOperationException("Task not found.");
            var visited = new HashSet<string>(StringComparer.Ordinal);
            void Visit(TaskRecord task)
            {
                if (!visited.Add(task.TaskId)) return;
                var children = task.ChildTaskIds.Where(id => tasks.ContainsKey(id)).Select(id => tasks[id])
                    .Where(child => child.ParentTaskId == task.TaskId && child.SessionId == task.SessionId).ToArray();
                foreach (var child in children) Visit(child);
                if (!TaskInterruption.IsActive(task)) return;
                TaskInterruption.Mark(task, cancelled, failureCode, children.Any(child => child.State == TaskState.NeedsReview));
                SaveTask(task);
            }
            Visit(root); return root;
        }

        private static string SerializeTask(TaskRecord task)
        {
            var artifacts = new JArray();
            foreach (var a in task.Artifacts)
            {
                artifacts.Add(new JObject
                {
                    ["path"] = a.Path, ["type"] = a.Type, ["bytes"] = a.Bytes, ["sha256"] = a.Sha256,
                    ["source_snapshot_sha256"] = a.SourceSnapshotSha256, ["tool"] = a.ToolName, ["tool_version"] = a.ToolVersion,
                    ["published_path"] = a.PublishedPath
                });
            }
            var obj = new JObject
            {
                ["task_id"] = task.TaskId,
                ["state"] = task.State.ToString(),
                ["sub_state"] = task.SubState.ToString(),
                ["kind"] = task.Kind.ToString(),
                ["execution_mode"] = task.ExecutionMode.ToString(),
                ["permission_mode"] = task.PermissionMode.ToString(),
                ["steps"] = JArray.FromObject(task.Steps),
                ["messages"] = JArray.FromObject(task.Messages),
                ["context_messages"] = JArray.FromObject(task.ContextMessages),
                ["context_summary"] = task.ContextSummary,
                ["context_compactions"] = task.ContextCompactionCount,
                ["parent_task_id"] = task.ParentTaskId, ["agent_name"] = task.AgentName, ["agent_model_alias"] = task.AgentModelAlias, ["agent_model_name"] = task.AgentModelName,
                ["dependency_task_ids"] = JArray.FromObject(task.DependencyTaskIds), ["child_task_ids"] = JArray.FromObject(task.ChildTaskIds), ["subagent_failures"] = task.SubAgentFailureCount, ["model_retry_count"] = task.ModelRetryCount, ["context_window_tokens"] = task.ContextWindowTokens, ["output_reserve_tokens"] = task.OutputReserveTokens, ["estimated_input_tokens"] = task.EstimatedInputTokens,
                ["snapshot_root"] = task.SnapshotRoot,
                ["skill_id"] = task.SkillId, ["skill_version"] = task.SkillVersion, ["skill_selection_source"] = task.SkillSelectionSource,
                ["agent_role_id"] = task.AgentRoleId, ["project_config_hash"] = task.ProjectConfigHash,
                ["expanded_request"] = task.ExpandedRequest, ["command_name"] = task.CommandName,
                ["calculation_evidence"] = task.HasCalculationEvidence,
                ["mutation_evidence"] = task.HasMutationEvidence,
                ["user_request"] = task.UserRequest,
                ["error_code"] = task.ErrorCode,
                ["error_message"] = task.ErrorMessage,
                ["created_utc"] = task.CreatedUtc.ToString("o"),
                ["finished_utc"] = task.FinishedUtc?.ToString("o"),
                ["workspace_root"] = task.WorkspaceRoot,
                ["final_text"] = task.FinalText,
                ["session_id"] = task.SessionId,
                ["turn_index"] = task.TurnIndex,
                ["approval_requested"] = task.ApprovalRequested,
                ["prompt_tokens"] = task.PromptTokens,
                ["completion_tokens"] = task.CompletionTokens,
                ["artifacts"] = artifacts
            };
            return obj.ToString(Formatting.Indented);
        }

        private static TaskRecord DeserializeTask(string json)
        {
            var obj = JObject.Parse(json);
            var task = new TaskRecord(
                (string)obj["task_id"]!, (string?)obj["user_request"] ?? "", (string?)obj["workspace_root"] ?? "")
            {
                State = Enum.TryParse((string?)obj["state"], out TaskState state) ? state : TaskState.Created,
                SubState = Enum.TryParse((string?)obj["sub_state"], out TaskSubState sub) ? sub : TaskSubState.None,
                Kind = Enum.TryParse((string?)obj["kind"], out TaskKind kind) ? kind : TaskKind.QuestionAnswer,
                ExecutionMode = Enum.TryParse((string?)obj["execution_mode"], out AgentExecutionMode mode) && Enum.IsDefined(typeof(AgentExecutionMode), mode) ? mode : AgentExecutionMode.Execute,
                PermissionMode = TaskPermissions.ParseStored(obj["permission_mode"]),
                Steps = obj["steps"]?.ToObject<List<StepRecord>>() ?? new List<StepRecord>(),
                SnapshotRoot = (string?)obj["snapshot_root"],
                SkillId = (string?)obj["skill_id"] ?? "", SkillVersion = (string?)obj["skill_version"] ?? "", SkillSelectionSource = (string?)obj["skill_selection_source"] ?? "",
                HasCalculationEvidence = (bool?)obj["calculation_evidence"] ?? false,
                HasMutationEvidence = (bool?)obj["mutation_evidence"] ?? false,
                ErrorCode = (string?)obj["error_code"],
                ErrorMessage = (string?)obj["error_message"],
                FinalText = (string?)obj["final_text"] ?? "",
                SessionId = (string?)obj["session_id"] ?? "",
                TurnIndex = (int?)obj["turn_index"] ?? 0,
                ApprovalRequested = (bool?)obj["approval_requested"] ?? false,
                PromptTokens = (long?)obj["prompt_tokens"] ?? 0,
                CompletionTokens = (long?)obj["completion_tokens"] ?? 0
            };
            if (DateTime.TryParse((string?)obj["created_utc"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var created))
            {
                task.CreatedUtc = created;
            }
            if (DateTime.TryParse((string?)obj["finished_utc"], null, System.Globalization.DateTimeStyles.RoundtripKind, out var finished))
            {
                task.FinishedUtc = finished;
            }
            task.Messages = obj["messages"]?.ToObject<List<ChatMessage>>() ?? new List<ChatMessage>();
            task.ContextMessages = obj["context_messages"]?.ToObject<List<ChatMessage>>() ?? new List<ChatMessage>();
            task.ContextSummary = (string?)obj["context_summary"] ?? "";
            task.ContextCompactionCount = (int?)obj["context_compactions"] ?? 0;
            task.ParentTaskId = (string?)obj["parent_task_id"] ?? ""; task.AgentName = (string?)obj["agent_name"] ?? "";
            task.AgentModelAlias = (string?)obj["agent_model_alias"] ?? "";
            task.AgentRoleId = (string?)obj["agent_role_id"] ?? "general";
            task.ProjectConfigHash = (string?)obj["project_config_hash"] ?? "";
            task.ExpandedRequest = (string?)obj["expanded_request"] ?? "";
            task.CommandName = (string?)obj["command_name"] ?? "";
            task.AgentModelName = (string?)obj["agent_model_name"] ?? "";
            task.ChildTaskIds = obj["child_task_ids"]?.ToObject<List<string>>() ?? new List<string>();
            task.DependencyTaskIds = obj["dependency_task_ids"]?.ToObject<List<string>>() ?? new List<string>();
            task.SubAgentFailureCount = (int?)obj["subagent_failures"] ?? 0;
            task.ModelRetryCount = (int?)obj["model_retry_count"] ?? 0;
            task.ContextWindowTokens = (int?)obj["context_window_tokens"] ?? 0; task.OutputReserveTokens = (int?)obj["output_reserve_tokens"] ?? 0; task.EstimatedInputTokens = (int?)obj["estimated_input_tokens"] ?? 0;
            foreach (var a in (JArray?)obj["artifacts"] ?? new JArray())
            {
                task.Artifacts.Add(new ArtifactInfo(
                    (string)a["path"]!, (string)a["type"]!, (long)a["bytes"]!, (string)a["sha256"]!, (string?)a["source_snapshot_sha256"] ?? "", (string?)a["tool"] ?? "", (string?)a["tool_version"] ?? "1") { PublishedPath = (string?)a["published_path"] ?? "" });
            }
            return task;
        }

        // ---- events.jsonl ---------------------------------------------------

        public string EventsPath(string taskId) => Path.Combine(TaskDir(taskId), "events.jsonl");

        public void AppendEvent(TaskEvent taskEvent)
        {
            var line = new JObject
            {
                ["event_id"] = taskEvent.EventId,
                ["utc_time"] = taskEvent.UtcTime.ToString("o"),
                ["task_id"] = taskEvent.TaskId,
                ["kind"] = taskEvent.Kind.ToString(),
                ["tool"] = taskEvent.ToolName,
                ["ok"] = taskEvent.ToolOk,
                ["summary"] = taskEvent.Summary
            };
            Directory.CreateDirectory(TaskDir(taskEvent.TaskId));
            _db.Append(taskEvent.TaskId, taskEvent.EventId, line.ToString(Formatting.None));
        }

        public List<TaskEvent> LoadEvents(string taskId)
        {
            var path = EventsPath(taskId);
            var result = new List<TaskEvent>();
            foreach (var line in _db.Events(taskId))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var obj = JObject.Parse(line);
                    if (!Enum.TryParse((string?)obj["kind"], out TaskEventKind kind) || !Enum.IsDefined(typeof(TaskEventKind), kind) ||
                        (string?)obj["task_id"] != taskId) continue;
                    DateTime utc;
                    if (obj["utc_time"]?.Type == JTokenType.Date) utc = obj["utc_time"]!.Value<DateTime>();
                    else if (!DateTime.TryParse((string?)obj["utc_time"], System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out utc)) continue;
                    result.Add(new TaskEvent(
                        (string)obj["event_id"]!,
                        utc,
                        (string)obj["task_id"]!,
                        kind,
                        (string?)obj["summary"] ?? "",
                        (string?)obj["tool"],
                        obj["ok"] != null && obj["ok"]!.Type != JTokenType.Null ? (bool)obj["ok"]! : null));
                }
                catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidCastException || ex is FormatException)
                {
                    // 跳过损坏行。
                }
            }
            return result;
        }

        // ---- sessions --------------------------------------------------------

        /// <summary>会话目录：store 根下的 _sessions（与任务目录隔离，独立于 store 根路径）。</summary>
        private string SessionsRoot => Path.Combine(_root, "_sessions");

        private string SessionPath(string sessionId)
        {
            if (sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException("非法 session_id");
            }
            return Path.Combine(SessionsRoot, sessionId, "session.json");
        }

        /// <summary>保存/更新会话记录。</summary>
        public void SaveSession(SessionRecord session)
        {
            _db.Put("session", session.SessionId, SerializeSession(session));
        }

        /// <summary>会话列表（最近优先）。</summary>
        public List<SessionRecord> LoadSessions()
        {
            return _db.List("session").Select(DeserializeSession).OrderByDescending(s => s.LastActiveUtc).ToList();
        }

        public SessionRecord? LoadSession(string sessionId)
        {
            var json = _db.Get("session", sessionId);
            return json == null ? null : DeserializeSession(json);
        }

        /// <summary>把一轮任务追加到会话（不存在则先保存会话骨架）。</summary>
        public void AppendTurn(SessionRecord session, SessionTurn turn)
        {
            session.Turns.Add(turn);
            session.LastActiveUtc = DateTime.UtcNow;
            SaveSession(session);
        }

        private static string SerializeSession(SessionRecord session)
        {
            var turns = new JArray();
            foreach (var t in session.Turns)
            {
                turns.Add(new JObject
                {
                    ["task_id"] = t.TaskId,
                    ["created_utc"] = t.CreatedUtc.ToString("o"),
                    ["request"] = t.Request
                });
            }
            var obj = new JObject
            {
                ["session_id"] = session.SessionId,
                ["project_id"] = session.ProjectId,
                ["title"] = session.Title,
                ["workspace_root"] = session.WorkspaceRoot,
                ["created_utc"] = session.CreatedUtc.ToString("o"),
                ["last_active_utc"] = session.LastActiveUtc.ToString("o"),
                ["turns"] = turns
            };
            return obj.ToString(Formatting.Indented);
        }

        private static SessionRecord DeserializeSession(string json)
        {
            var obj = JObject.Parse(json);
            var session = new SessionRecord(
                (string?)obj["session_id"] ?? "",
                (string?)obj["title"] ?? "",
                (string?)obj["workspace_root"] ?? "")
            {
                Title = (string?)obj["title"] ?? "",
                ProjectId = (string?)obj["project_id"] ?? "",
                WorkspaceRoot = (string?)obj["workspace_root"] ?? ""
            };
            if (DateTime.TryParse((string?)obj["created_utc"], null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var created))
            {
                session.CreatedUtc = created;
            }
            if (DateTime.TryParse((string?)obj["last_active_utc"], null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var active))
            {
                session.LastActiveUtc = active;
            }
            foreach (var t in (JArray?)obj["turns"] ?? new JArray())
            {
                session.Turns.Add(new SessionTurn
                {
                    TaskId = (string?)t["task_id"] ?? "",
                    Request = (string?)t["request"] ?? "",
                    CreatedUtc = DateTime.TryParse((string?)t["created_utc"], null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var turnCreated)
                        ? turnCreated
                        : DateTime.MinValue
                });
            }
            return session;
        }

        // ---- receipts.json --------------------------------------------------

        public string ReceiptsPath(string taskId) => Path.Combine(TaskDir(taskId), "receipts.json");

        public void SaveReceipts(string taskId, IEnumerable<ToolReceipt> receipts)
        {
            var arr = new JArray();
            foreach (var r in receipts)
            {
                arr.Add(new JObject
                {
                    ["tool_call_id"] = r.ToolCallId,
                    ["tool_name"] = r.ToolName,
                    ["arguments_json"] = r.ArgumentsJson,
                    ["ok"] = r.Ok,
                    ["result_json"] = r.ResultJson
                });
            }
            _db.Put("receipts", taskId, arr.ToString(Formatting.None));
        }

        public List<ToolReceipt> LoadReceipts(string taskId)
        {
            var path = ReceiptsPath(taskId);
            var json = _db.Get("receipts", taskId);
            if (json == null) return new List<ToolReceipt>();
            var arr = JArray.Parse(json);
            return arr.Select(r => new ToolReceipt(
                (string)r["tool_call_id"]!, (string)r["tool_name"]!,
                (string)r["arguments_json"]!, (bool)r["ok"]!, (string)r["result_json"]!)).ToList();
        }
    }

    /// <summary>任务事件落地 + 每任务回执存储（写 receipts.json）。</summary>
    public sealed class LocalTaskEventSink : ITaskEventSink, IToolReceiptStore, ITaskCheckpointSink
    {
        private readonly LocalTaskStore _store;
        private readonly object _lock = new();
        private readonly Dictionary<string, ToolReceipt> _receipts = new(StringComparer.Ordinal);

        public event Action<TaskEvent>? EventAppended;

        public LocalTaskEventSink(LocalTaskStore store, string taskId)
        {
            _store = store;
            foreach (var r in store.LoadReceipts(taskId)) _receipts[taskId + "\0" + r.ToolCallId] = r;
        }

        public void SaveCheckpoint(TaskRecord task) => _store.SaveTask(task);

        public void Append(TaskEvent taskEvent)
        {
            lock (_lock)
            {
                if (taskEvent.Kind != TaskEventKind.AssistantDelta)
                {
                    string summary = taskEvent.Kind == TaskEventKind.TaskStarted ? "任务开始" :
                        taskEvent.Kind == TaskEventKind.TaskFailed ? "任务失败（详情保存在任务状态）" :
                        taskEvent.Kind == TaskEventKind.ToolCallFinished && taskEvent.ToolOk == false ? "工具失败（详情保存在任务状态）" : taskEvent.Summary;
                    _store.AppendEvent(new TaskEvent(taskEvent.EventId, taskEvent.UtcTime, taskEvent.TaskId, taskEvent.Kind, summary, taskEvent.ToolName, taskEvent.ToolOk));
                }
            }
            EventAppended?.Invoke(taskEvent);
        }

        public ToolReceipt? Find(string taskId, string toolCallId)
        {
            lock (_lock)
            {
                return _receipts.TryGetValue(taskId + " " + toolCallId, out var r) ? r : null;
            }
        }

        public void Put(string taskId, ToolReceipt receipt)
        {
            lock (_lock)
            {
                _receipts[taskId + " " + receipt.ToolCallId] = receipt;
                _store.SaveReceipts(taskId, _receipts.Values);
            }
        }
    }
}
