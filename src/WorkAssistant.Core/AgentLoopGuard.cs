using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Core
{
    /// <summary>Per-run protocol validation and consecutive identical-call protection; contains no IO.</summary>
    public sealed class AgentLoopGuard
    {
        public const int RepeatedCallLimit = 3;
        public const int MaxArgumentsChars = 256 * 1024;
        private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
        private string? _lastSignature;
        private int _repeats;

        // Validate the entire batch before any of its tools can execute.
        public void ValidateReply(ModelReply reply)
        {
            if (!reply.HasToolCalls) return;
            if (reply.FinishReason != "tool_calls")
                throw new ToolException(ErrorCodes.ModelProtocolError, "模型工具调用没有正常结束，未执行本批操作。");
            var batch = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in reply.ToolCalls!)
            {
                if (string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 256 ||
                    string.IsNullOrWhiteSpace(call.Name) || call.Name.Length > 128 ||
                    call.ArgumentsJson == null || call.ArgumentsJson.Length > MaxArgumentsChars ||
                    _callIds.Contains(call.Id) || !batch.Add(call.Id))
                    throw new ToolException(ErrorCodes.ModelProtocolError, "模型返回无效或重复的工具调用标识，未执行本批操作。");
            }
            foreach (var id in batch) _callIds.Add(id);
        }

        public void BeforeTool(ToolCall call)
        {
            string arguments;
            try
            {
                using var text = new StringReader(call.ArgumentsJson);
                using var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None, MaxDepth = 64 };
                var parsed = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                if (reader.Read()) throw new JsonReaderException("Trailing JSON");
                arguments = Canonicalize(parsed).ToString(Formatting.None);
            }
            catch (JsonException) { arguments = call.ArgumentsJson; } // Tool validation reports malformed JSON.
            var signature = call.Name + "\n" + arguments;
            _repeats = string.Equals(signature, _lastSignature, StringComparison.Ordinal) ? _repeats + 1 : 1;
            _lastSignature = signature;
            if (_repeats >= RepeatedCallLimit)
                throw new ToolException(ErrorCodes.ToolLoopDetected,
                    "模型连续请求同一工具和相同参数，已在第 " + RepeatedCallLimit + " 次执行前停止，避免重复操作。请补充要求或调整任务范围。");
        }

        private static JToken Canonicalize(JToken value)
        {
            if (value is JObject obj)
                return new JObject(obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal)
                    .Select(p => new JProperty(p.Name, Canonicalize(p.Value))));
            if (value is JArray array) return new JArray(array.Select(Canonicalize));
            return value.DeepClone();
        }
    }
}
