using Newtonsoft.Json.Linq;

namespace WorkAssistant.Core
{
    /// <summary>Shared Chat Completions wire contract used by request budgeting and HTTP transport.</summary>
    public static class ModelRequestJson
    {
        public static string Serialize(ModelRequest request, bool stream = false)
        {
            var messages = new JArray();
            foreach (var m in request.Messages)
            {
                var obj = new JObject { ["role"] = m.Role };
                if (m.Role == "assistant" && m.ReasoningContent != null && request.Tools.Count > 0) obj["reasoning_content"] = m.ReasoningContent;
                if (m.Content != null)
                {
                    obj["content"] = m.Content;
                }
                if (m.ToolCalls != null)
                {
                    var calls = new JArray();
                    foreach (var c in m.ToolCalls)
                    {
                        calls.Add(new JObject
                        {
                            ["id"] = c.Id,
                            ["type"] = "function",
                            ["function"] = new JObject
                            {
                                ["name"] = c.Name,
                                ["arguments"] = c.ArgumentsJson
                            }
                        });
                    }
                    obj["tool_calls"] = calls;
                }
                if (m.ToolCallId != null)
                {
                    obj["tool_call_id"] = m.ToolCallId;
                }
                messages.Add(obj);
            }

            var tools = new JArray();
            foreach (var t in request.Tools)
            {
                tools.Add(new JObject
                {
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.Parameters.DeepClone()
                    }
                });
            }

            var payload = new JObject
            {
                ["model"] = request.Model,
                ["stream"] = stream,
                ["messages"] = messages
            };
            if (tools.Count > 0) { payload["tools"] = tools; payload["tool_choice"] = request.ToolChoice; }
            if (request.MaxOutputTokens > 0) payload["max_tokens"] = request.MaxOutputTokens;
            // 供应商扩展字段（strict / stream_options 等）默认不发送（设计 §8.2）。
            return payload.ToString(Newtonsoft.Json.Formatting.None);
        }

    }
}
