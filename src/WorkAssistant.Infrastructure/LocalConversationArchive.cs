using System;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    public sealed class LocalConversationArchive : ITaskHistoryReader
    {
        private readonly LocalTaskStore _store;
        private readonly string _currentTaskId;
        public LocalConversationArchive(LocalTaskStore store, string taskId) { _store = store; _currentTaskId = taskId; }
        private string[] Allowed()
        {
            var current = _store.LoadTask(_currentTaskId) ?? throw new ToolException(ErrorCodes.PathDenied, "当前任务不存在。");
            var session = string.IsNullOrEmpty(current.SessionId) ? null : _store.LoadSession(current.SessionId);
            return (session?.Turns.Select(t => t.TaskId) ?? new string[0]).Concat(new[] { _currentTaskId }).Distinct().ToArray();
        }
        public string Describe() => "当前任务 ID：" + _currentTaskId + "。可回查的本会话任务 ID（最近 20 个，含本轮；原始结果通过 read_task_history 读取）：" + string.Join(", ", Allowed().Reverse().Take(20));
        public string Read(string taskId, string section)
        {
            if (!Guid.TryParseExact(taskId, "N", out _)) throw new ToolException(ErrorCodes.PathDenied, "只能回查当前会话中的任务。");
            var allowed = Allowed();
            var task = _store.LoadTask(taskId) ?? throw new ToolException(ErrorCodes.InvalidArguments, "任务记录不存在。");
            if (!allowed.Contains(taskId))
            {
                var current = _store.LoadTask(_currentTaskId)!;
                if (string.IsNullOrEmpty(task.ParentTaskId) || !allowed.Contains(task.ParentTaskId) || task.SessionId != current.SessionId)
                    throw new ToolException(ErrorCodes.PathDenied, "只能回查当前会话的主任务及其子任务。");
            }
            if (section == "inputs")
            {
                string manifest = Path.Combine(_store.Root, taskId, "snapshot", "manifest.json");
                if (!File.Exists(manifest)) return "{\"files\":[],\"notice\":\"输入快照尚未建立或已被保留策略清理\"}";
                if (new FileInfo(manifest).Length > 1024 * 1024) throw new ToolException(ErrorCodes.LimitExceeded, "输入快照清单超过读取上限。");
                return Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(manifest)).ToString(Formatting.None);
            }
            object data = section switch {
                "steps" => task.Steps,
                "messages" => task.Messages,
                "artifacts" => task.Artifacts,
                "summary" => new { task.TaskId, task.ParentTaskId, task.DependencyTaskIds, task.AgentName, task.AgentModelAlias, task.AgentModelName, task.UserRequest, task.State, task.FinalText, task.ContextSummary, task.ContextCompactionCount, task.ContextWindowTokens, task.OutputReserveTokens, task.EstimatedInputTokens, task.ModelRetryCount, task.ErrorCode },
                _ => throw new ToolException(ErrorCodes.InvalidArguments, "section 只能为 steps/messages/artifacts/summary/inputs。")
            };
            return JsonConvert.SerializeObject(data, Formatting.None);
        }
    }
}
