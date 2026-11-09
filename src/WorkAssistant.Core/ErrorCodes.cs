using System;

namespace WorkAssistant.Core
{
    /// <summary>设计文档第 12 节定义的稳定错误码 + 正式版（W02）补充。</summary>
    public static class ErrorCodes
    {
        public const string DependencyFailed = "DEPENDENCY_FAILED";
        public const string EditConflict = "EDIT_CONFLICT";
        public const string AtomicWriteUnavailable = "ATOMIC_WRITE_UNAVAILABLE";
        public const string ContextLengthExceeded = "CONTEXT_LENGTH_EXCEEDED";
        public const string ModelFeatureUnsupported = "MODEL_FEATURE_UNSUPPORTED";
        public const string ModelAuthFailed = "MODEL_AUTH_FAILED";
        public const string ModelUnavailable = "MODEL_UNAVAILABLE";
        public const string ModelProtocolError = "MODEL_PROTOCOL_ERROR";
        public const string ToolCallingUnsupported = "TOOL_CALLING_UNSUPPORTED";
        public const string PathDenied = "PATH_DENIED";
        public const string InvalidArguments = "INVALID_ARGUMENTS";
        public const string CsvInvalid = "CSV_INVALID";
        public const string LimitExceeded = "LIMIT_EXCEEDED";
        public const string ToolLoopDetected = "TOOL_LOOP_DETECTED";
        public const string ToolPolicyDenied = "TOOL_POLICY_DENIED";
        public const string OutputWriteFailed = "OUTPUT_WRITE_FAILED";
        public const string NoArtifacts = "NO_ARTIFACTS";
        public const string Cancelled = "CANCELLED";

        // ---- 正式版 1.0 补充（PRODUCTION_IMPLEMENTATION_PLAN §5.5/§8.4/§9.2）----

        /// <summary>用户拒绝了需要确认的操作（文件修改等）。</summary>
        public const string UserApprovalDenied = "USER_APPROVAL_DENIED";
        /// <summary>文件格式不支持或无法解析（如扫描 PDF、加密文档）。</summary>
        public const string UnsupportedFormat = "UNSUPPORTED_FORMAT";
        /// <summary>文件整理计划校验失败（越界/冲突/非法操作）。</summary>
        public const string PlanInvalid = "PLAN_INVALID";
        /// <summary>副作用处于不确定状态（部分完成），需要人工复核，不自动重试。</summary>
        public const string NeedsReview = "NEEDS_REVIEW";
        public const string WorkerUnavailable = "WORKER_UNAVAILABLE";
        public const string HostInitializationFailed = "HOST_INITIALIZATION_FAILED";
        public const string HostExecutionFailed = "HOST_EXECUTION_FAILED";
        public const string RemoteKernelUnavailable = "REMOTE_KERNEL_UNAVAILABLE";
        public const string SubAgentFailed = "SUBAGENT_FAILED";
        public const string ModelResponseTooLarge = "MODEL_RESPONSE_TOO_LARGE";
        public const string ModelOutputTruncated = "MODEL_OUTPUT_TRUNCATED";
    }

    /// <summary>
    /// 带稳定错误码的领域异常。code 面向程序与日志，message 面向用户，
    /// 二者均不得包含 API Key、原始堆栈或未授权路径内容。
    /// </summary>
    [Serializable]
    public class ToolException : Exception
    {
        public string Code { get; }

        public ToolException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public ToolException(string code, string message, Exception inner)
            : base(message, inner)
        {
            Code = code;
        }
    }
}
