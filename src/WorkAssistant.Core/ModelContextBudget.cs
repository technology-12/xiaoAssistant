using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace WorkAssistant.Core
{
    // OpenCode-inspired context minus output reserve. Estimates are deliberately conservative, not billing tokens.
    public sealed class ModelContextBudget
    {
        public int ContextTokens { get; }
        public int OutputTokens { get; }
        public int UsableTokens => ContextTokens - OutputTokens;
        public ModelContextBudget(int contextTokens = 0, int outputTokens = 0)
        {
            if (contextTokens != 0 && (contextTokens < 4096 || contextTokens > 2000000) || outputTokens < 0 || outputTokens > 200000 || contextTokens > 0 && outputTokens >= contextTokens)
                throw new ToolException(ErrorCodes.InvalidArguments, "模型上下文窗口或输出额度无效；输出必须小于上下文窗口。");
            ContextTokens = contextTokens; OutputTokens = outputTokens == 0 && contextTokens > 0 ? Math.Min(4096, contextTokens / 4) : outputTokens;
        }
        public static int Estimate(string value)
        {
            long ascii = 0, other = 0;
            foreach (char c in value) { if (c <= 127) ascii++; else other++; }
            return (int)Math.Min(int.MaxValue, (ascii + 2) / 3 + other * 2);
        }
        public int Estimate(ModelRequest request) => Estimate(ModelRequestJson.Serialize(request)) + 256;
        public bool ShouldCompact(ModelRequest request) => ModelRequestJson.Serialize(request).Length > ConversationContextManager.SoftLimitChars ||
            ContextTokens > 0 && Estimate(request) >= UsableTokens * 0.85;
        public bool Fits(ModelRequest request) => ModelRequestJson.Serialize(request).Length <= AgentController.MaxConversationChars &&
            (ContextTokens == 0 || Estimate(request) <= UsableTokens);
        public void Demand(ModelRequest request)
        {
            if (ModelRequestJson.Serialize(request).Length > AgentController.MaxConversationChars)
                throw new ToolException(ErrorCodes.LimitExceeded, "本机请求超过 512K 字符上限（含工具定义）；请缩小材料范围。原始记录已保留。");
            int estimate = Estimate(request);
            if (ContextTokens > 0 && estimate > UsableTokens) throw new ToolException(ErrorCodes.LimitExceeded,
                "估算输入 " + estimate + " tokens（含工具定义）加输出预留 " + OutputTokens + "，超过窗口 " + ContextTokens +
                "；请调整模型预算、选择工具更少的技能或缩小材料。原始记录已保留。");
        }
        public int PruneOldToolResults(List<ChatMessage> messages, TaskRecord task)
        {
            var groups = ConversationContextManager.CompleteGroups(messages.Skip(1).ToArray()); int changed = 0;
            // Retain recent bundles and full skill instructions; only project old tool output, never delete archive data.
            foreach (var group in groups.Take(Math.Max(0, groups.Count - 3)))
            {
                foreach (var tool in group.Where(m => m.Role == "tool" && m.Content?.Length > 2048))
                {
                    if (!task.Messages.Contains(tool)) continue;
                    var call = group[0].ToolCalls?.FirstOrDefault(c => c.Id == tool.ToolCallId);
                    if (call?.Name == "read_skill" || call?.Name == "list_skills") continue;
                    int index = messages.IndexOf(tool); if (index < 0) continue;
                    var note = JsonConvert.SerializeObject(new { archived = true, task_id = task.TaskId, tool_call_id = tool.ToolCallId,
                        notice = "较早的工具结果已从模型上下文投影中收起；需要精确结果请用 read_task_history 回查该任务 steps。不可据此补造结果或授权。" });
                    messages[index] = new ChatMessage("tool", note, toolCallId: tool.ToolCallId); changed++;
                }
            }
            return changed;
        }
    }
}
