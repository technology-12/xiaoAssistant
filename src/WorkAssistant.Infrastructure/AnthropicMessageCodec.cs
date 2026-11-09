using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    /// <summary>Text and client-tool Messages protocol. Opaque thinking, media and server tools are not enabled.</summary>
    public static class AnthropicMessageCodec
    {
        public static string Serialize(ModelRequest request, bool stream)
        {
            var system = new List<string>(); var messages = new JArray();
            var pending = new HashSet<string>(StringComparer.Ordinal);
            foreach (var message in request.Messages)
            {
                if (pending.Count > 0 && message.Role != "tool") throw Invalid("工具结果必须紧跟对应的调用。");
                if (message.Role == "system") { if (!string.IsNullOrEmpty(message.Content)) system.Add(message.Content!); continue; }
                var blocks = new JArray(); string role;
                if (message.Role == "tool")
                {
                    if (message.ToolCallId == null || !pending.Remove(message.ToolCallId)) throw Invalid("出现未配对或重复的工具结果。");
                    var result = new JObject { ["type"] = "tool_result", ["tool_use_id"] = message.ToolCallId, ["content"] = message.Content ?? "" };
                    try { if (JObject.Parse(message.Content ?? "{}")["ok"]?.Value<bool>() == false) result["is_error"] = true; }
                    catch (Exception ex) when (ex is JsonException || ex is InvalidCastException || ex is FormatException) { }
                    blocks.Add(result); role = "user";
                }
                else
                {
                    if (message.Role != "user" && message.Role != "assistant") throw Invalid("不支持该消息角色。");
                    role = message.Role;
                    if (!string.IsNullOrEmpty(message.Content)) blocks.Add(new JObject { ["type"] = "text", ["text"] = message.Content });
                    foreach (var call in message.ToolCalls ?? XpArray.Empty<ToolCall>())
                    {
                        if (role != "assistant" || string.IsNullOrWhiteSpace(call.Id) || !pending.Add(call.Id)) throw Invalid("工具调用标识或角色无效。");
                        JObject input;
                        try { input = JObject.Parse(call.ArgumentsJson); }
                        catch (JsonException) { throw Invalid("工具参数必须为完整 JSON 对象。"); }
                        blocks.Add(new JObject { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = input });
                    }
                }
                if (blocks.Count == 0) continue;
                if (messages.Last is JObject last && (string?)last["role"] == role)
                    foreach (var block in blocks.ToArray()) ((JArray)last["content"]!).Add(block.DeepClone());
                else messages.Add(new JObject { ["role"] = role, ["content"] = blocks });
            }
            if (pending.Count > 0 || messages.Count == 0 || (string?)messages[0]!["role"] != "user") throw Invalid("Messages 输入不完整或缺少用户消息。");
            var payload = new JObject { ["model"] = request.Model, ["max_tokens"] = request.MaxOutputTokens > 0 ? request.MaxOutputTokens : 4096,
                ["stream"] = stream, ["messages"] = messages };
            if (system.Count > 0) payload["system"] = string.Join("\n\n", system);
            if (request.Tools.Count > 0)
            {
                payload["tools"] = new JArray(request.Tools.Select(t => new JObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.Parameters.DeepClone() }));
                string choice = request.ToolChoice == "required" ? "any" : request.ToolChoice;
                if (choice != "auto" && choice != "any" && choice != "none") throw Invalid("不支持的工具选择策略。");
                payload["tool_choice"] = new JObject { ["type"] = choice };
            }
            return payload.ToString(Formatting.None);
        }
        public static ModelReply Parse(string body, bool streamed = false)
        {
            try { return ParseObject(JObject.Parse(body), streamed); }
            catch (JsonException) { throw Protocol("Messages 响应不是合法 JSON。"); }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            { throw Protocol("Messages 响应字段类型无效。"); }
        }
        private static ModelReply ParseObject(JObject root, bool streamed)
        {
            if ((string?)root["type"] == "error") throw ServiceError(root);
            if ((string?)root["type"] != "message" || (string?)root["role"] != "assistant" || root["content"] is not JArray blocks || blocks.Count > 128)
                throw Protocol("Messages 响应结构不完整。");
            if ((string?)root["stop_reason"] == "max_tokens")
            {
                var cutUsage = root["usage"] as JObject;
                throw new ModelOutputTruncatedException(cutUsage == null ? null : new ModelUsage(checked(Count(cutUsage, "input_tokens") + Count(cutUsage, "cache_read_input_tokens") + Count(cutUsage, "cache_creation_input_tokens")), Count(cutUsage, "output_tokens")), string.Concat(blocks.OfType<JObject>().Where(b => (string?)b["type"] == "text").Select(b => (string?)b["text"])), blocks.ToString(Formatting.None));
            }
            if (blocks.Any(b => b is not JObject)) throw Protocol("Messages 内容块必须为对象。");
            var text = new StringBuilder(); var calls = new List<ToolCall>(); var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in blocks.OfType<JObject>())
            {
                switch ((string?)block["type"])
                {
                    case "text":
                        if (block["text"]?.Type != JTokenType.String) throw Protocol("文本内容类型无效。");
                        text.Append((string)block["text"]!); break;
                    case "tool_use":
                        var id = (string?)block["id"]; var name = (string?)block["name"];
                        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !ids.Add(id!) || block["input"] is not JObject input || calls.Count >= AgentController.MaxToolCalls)
                            throw Protocol("工具调用结构或标识无效。");
                        var args = input.ToString(Formatting.None);
                        ModelResponseLimits.DemandArguments(args.Length);
                        calls.Add(new ToolCall(id!, name!, args)); break;
                    default: throw new ToolException(ErrorCodes.ModelFeatureUnsupported, "该 Messages 内容类型尚未适配；未执行其中的工具。");
                }
            }
            string? reason = (string?)root["stop_reason"];
            if (reason == "max_tokens")
            {
                var rawUsage = root["usage"] as JObject;
                throw new ModelOutputTruncatedException(rawUsage == null ? null : new ModelUsage(checked(Count(rawUsage, "input_tokens") + Count(rawUsage, "cache_read_input_tokens") + Count(rawUsage, "cache_creation_input_tokens")), Count(rawUsage, "output_tokens")), text.ToString(), string.Join("\n", calls.Select(c => c.Name + ": " + c.ArgumentsJson)));
            }
            if (reason != "end_turn" && reason != "tool_use" && reason != "stop_sequence") throw Protocol("模型响应未完整结束：" + (reason ?? "缺少 stop_reason"));
            if ((reason == "tool_use") != (calls.Count > 0) || calls.Count == 0 && text.Length == 0) throw Protocol("结束原因与消息内容不一致。");
            ModelUsage? usage = null;
            if (root["usage"] is JObject raw)
            {
                long input = checked(Count(raw, "input_tokens") + Count(raw, "cache_read_input_tokens") + Count(raw, "cache_creation_input_tokens"));
                usage = new ModelUsage(input, Count(raw, "output_tokens"));
            }
            ModelResponseLimits.DemandText(text.Length);
            return new ModelReply(text.Length == 0 ? null : text.ToString(), calls.Count == 0 ? null : calls, calls.Count == 0 ? "stop" : "tool_calls", streamed, usage);
        }
        private static long Count(JObject usage, string key)
        {
            if (usage[key] == null) return 0;
            if (usage[key]!.Type != JTokenType.Integer || usage[key]!.Value<long>() < 0) throw Protocol("用量字段无效。");
            return usage[key]!.Value<long>();
        }
        public static async Task<ModelReply> ParseStreamAsync(Stream stream, Action<string> delta, CancellationToken token)
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
            using var registration = token.Register(() => { try { stream.Dispose(); } catch (ObjectDisposedException) { } });
            var blocks = new SortedDictionary<int, JObject>(); var args = new Dictionary<int, StringBuilder>(); var open = new HashSet<int>();
            var texts = new Dictionary<int, StringBuilder>();
            var usage = new JObject(); bool started = false, stopped = false, invalidArguments = false; string? reason = null;
            string eventName = ""; var data = new StringBuilder();
            void Event()
            {
                if (data.Length == 0) return;
                JObject evt;
                try { evt = JObject.Parse(data.ToString()); } catch (JsonException) { throw Protocol("流式事件不是完整 JSON。"); }
                var type = (string?)evt["type"];
                if (eventName.Length > 0 && eventName != type) throw Protocol("流式事件名与类型不一致。");
                if (type == "error") throw ServiceError(evt);
                if (type == "ping") return;
                if (type == "message_start")
                {
                    if (started || (string?)evt["message"]?["role"] != "assistant") throw Protocol("流式消息开始事件无效。");
                    started = true;
                    if (evt["message"]?["usage"] is JObject initial) foreach (var p in initial.Properties()) usage[p.Name] = p.Value.DeepClone();
                    return;
                }
                if (!started || stopped) throw Protocol("流式事件顺序无效。");
                if (type == "content_block_start")
                {
                    int index = Index(evt);
                    if (blocks.ContainsKey(index) || evt["content_block"] is not JObject block) throw Protocol("内容块重复或无效。");
                    var blockType = (string?)block["type"];
                    if (blockType != "text" && blockType != "tool_use") throw new ToolException(ErrorCodes.ModelFeatureUnsupported, "尚未适配该流式内容块。");
                    blocks.Add(index, (JObject)block.DeepClone()); open.Add(index); args[index] = new StringBuilder();
                    texts[index] = new StringBuilder(blockType == "text" ? (string?)block["text"] ?? "" : "");
                    if (blockType == "text" && block["text"]?.Type == JTokenType.String && ((string)block["text"]!).Length > 0) delta((string)block["text"]!);
                }
                else if (type == "content_block_delta")
                {
                    int index = Index(evt); if (!open.Contains(index)) throw Protocol("内容增量没有对应的打开块。");
                    var block = blocks[index]; var change = evt["delta"] as JObject ?? throw Protocol("内容增量无效。");
                    if ((string?)change["type"] == "text_delta" && (string?)block["type"] == "text" && change["text"]?.Type == JTokenType.String)
                    { var part = (string)change["text"]!; texts[index].Append(part); ModelResponseLimits.DemandText(texts.Values.Sum(t => t.Length)); if (part.Length > 0) delta(part); }
                    else if ((string?)change["type"] == "input_json_delta" && (string?)block["type"] == "tool_use" && change["partial_json"]?.Type == JTokenType.String)
                    { args[index].Append((string)change["partial_json"]!); ModelResponseLimits.DemandArguments(args[index].Length); }
                    else throw Protocol("内容增量类型与块不匹配。");
                }
                else if (type == "content_block_stop")
                {
                    int index = Index(evt); if (!open.Remove(index)) throw Protocol("内容块结束事件无效。");
                    if ((string?)blocks[index]["type"] == "text") blocks[index]["text"] = texts[index].ToString();
                    if (args[index].Length > 0)
                    {
                        if (blocks[index]["input"] is JObject prior && prior.Count > 0) throw Protocol("工具参数出现重复来源。");
                        try { blocks[index]["input"] = JObject.Parse(args[index].ToString()); } catch (JsonException) { invalidArguments = true; blocks[index]["input"] = new JObject(); }
                    }
                }
                else if (type == "message_delta")
                { if (evt["delta"]?["stop_reason"]?.Type == JTokenType.String) reason = (string)evt["delta"]!["stop_reason"]!; if (evt["usage"] is JObject update) foreach (var p in update.Properties()) usage[p.Name] = p.Value.DeepClone(); }
                else if (type == "message_stop")
                { if (open.Count > 0 || reason == null) throw Protocol("消息结束时仍有未完成内容。"); stopped = true; }
                // Additional non-content event types can be ignored; unknown content never executes.
            }
            string? line;
            while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
            {
                token.ThrowIfCancellationRequested();
                if (line.Length == 0)
                { Event(); data.Clear(); eventName = ""; if (stopped) break; }
                else if (line.StartsWith("event:", StringComparison.Ordinal)) eventName = line.Substring(6).Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(line.Substring(5).TrimStart()); }
            }
            if (!stopped && data.Length > 0) Event();
            if (!started || !stopped) throw new ModelHttpException(ErrorCodes.ModelUnavailable, "Messages 流式响应中断，未认定部分工具参数为成功。", null);
            if (reason == "max_tokens") throw new ModelOutputTruncatedException(new ModelUsage(checked(Count(usage, "input_tokens") + Count(usage, "cache_read_input_tokens") + Count(usage, "cache_creation_input_tokens")), Count(usage, "output_tokens")), string.Concat(texts.OrderBy(p => p.Key).Select(p => p.Value.ToString())), string.Join("\n", args.OrderBy(p => p.Key).Select(p => p.Value.ToString())));
            if (invalidArguments) throw Protocol("工具参数未完整收齐。");
            return Parse(new JObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JArray(blocks.Values), ["stop_reason"] = reason, ["usage"] = usage }.ToString(Formatting.None), true);
        }
        private static int Index(JObject obj)
        { if (obj["index"]?.Type != JTokenType.Integer || obj["index"]!.Value<int>() < 0 || obj["index"]!.Value<int>() >= 128) throw Protocol("内容块索引无效。"); return obj["index"]!.Value<int>(); }
        private static ToolException Invalid(string message) => new ToolException(ErrorCodes.InvalidArguments, message);
        private static ToolException Protocol(string message) => new ToolException(ErrorCodes.ModelProtocolError, message);
        internal static ModelHttpException ServiceError(JObject root)
        {
            var type = (string?)root["error"]?["type"]; var category = ModelServiceError.Category(root.ToString(Formatting.None));
            int status = type == "overloaded_error" ? 529 : type == "rate_limit_error" ? 429 : type == "authentication_error" ? 401 : type == "api_error" ? 500 : 400;
            if (status != 400) category = "";
            var code = status == 401 ? ErrorCodes.ModelAuthFailed : category == "context-overflow" ? ErrorCodes.ContextLengthExceeded : category == "tools-unsupported" ? ErrorCodes.ToolCallingUnsupported : status == 529 || status == 429 || status == 500 ? ErrorCodes.ModelUnavailable : ErrorCodes.ModelProtocolError;
            return new ModelHttpException(code, "Messages 服务返回错误；响应正文未写入诊断。", null, status, category: category);
        }
    }
}
