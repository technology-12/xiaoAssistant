using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    public sealed class HostFailureInfo
    {
        [JsonProperty("code")] public string Code { get; set; } = ErrorCodes.WorkerUnavailable;
        [JsonProperty("stage")] public string Stage { get; set; } = "任务宿主";
        [JsonProperty("exceptionType")] public string ExceptionType { get; set; } = "";
        [JsonProperty("message")] public string Message { get; set; } = "任务宿主异常退出。";
        [JsonProperty("methods")] public List<string> Methods { get; set; } = new();
        public static HostFailureInfo Capture(Exception exception, string stage, string apiKey)
        {
            var cause = exception is ToolException ? exception : exception.GetBaseException();
            string message = cause is ToolException tool ? tool.Message : "内部执行错误，请查看执行详情中的失败阶段与方法记录。";
            if (!string.IsNullOrEmpty(apiKey)) message = message.Replace(apiKey, "[已隐藏]");
            if (message.Length > 512) message = message.Substring(0, 512);
            return new HostFailureInfo {
                Code = cause is ToolException error ? error.Code : stage == "运行任务" ? ErrorCodes.HostExecutionFailed : ErrorCodes.HostInitializationFailed,
                Stage = stage, ExceptionType = cause.GetType().Name, Message = message,
                // Method identities only: no exception body, arguments, filenames, model credentials or document content.
                Methods = (new StackTrace(cause, false).GetFrames() ?? XpArray.Empty<StackFrame>()).Take(12)
                    .Select(f => f.GetMethod()).Where(m => m != null).Select(m => m!.DeclaringType?.FullName + "." + m.Name).ToList()
            };
        }
    }
    public sealed class HostExecutionException : IOException
    {
        public HostFailureInfo Failure { get; }
        public HostExecutionException(HostFailureInfo failure)
            : base(failure.Message + " 阶段：" + failure.Stage + "；异常：" + failure.ExceptionType) { Failure = failure; }
    }
}
