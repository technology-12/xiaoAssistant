using System;

namespace WorkAssistant.Core
{
    public sealed class ModelRetryInfo
    {
        public int RetryNumber { get; }
        public int? StatusCode { get; }
        public TimeSpan Delay { get; }
        public ModelRetryInfo(int retryNumber, int? statusCode, TimeSpan delay)
        { RetryNumber = retryNumber; StatusCode = statusCode; Delay = delay; }
    }
    public interface IModelRetrySource
    {
        event Action<ModelRetryInfo>? Retrying;
    }
}
