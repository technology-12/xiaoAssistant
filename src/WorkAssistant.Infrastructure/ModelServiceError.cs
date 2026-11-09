using System;
using Newtonsoft.Json.Linq;

namespace WorkAssistant.Infrastructure
{
    public static class ModelServiceError
    {
        public static string ForStatus(int status, string body) => status == 400 || status == 413 || status == 415 || status == 422 ? Category(body) : "";
        public static string Category(string body)
        {
            try
            {
                var obj = JObject.Parse(body); var error = obj["error"] as JObject ?? obj;
                var code = ((string?)error["code"] ?? (string?)error["type"] ?? "").ToLowerInvariant();
                var param = ((string?)error["param"] ?? "").ToLowerInvariant();
                var message = ((string?)error["message"] ?? "").ToLowerInvariant();
                bool unsupported = code == "unsupported_parameter" || code == "unsupported_value" || message.Contains("not support") || message.Contains("unsupported");
                if (code == "context_length_exceeded" || code == "prompt_too_long" || message.Contains("maximum context length") || message.Contains("prompt is too long")) return "context-overflow";
                if (unsupported && (param == "stream" || message.Contains("streaming") || message.Contains("stream is") || message.Contains("stream parameter"))) return "stream-unsupported";
                if (unsupported && (param == "tools" || message.Contains("tool use") || message.Contains("tool calling") || message.Contains("tools"))) return "tools-unsupported";
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is ArgumentException || ex is InvalidCastException) { }
            return "";
        }
    }
}
