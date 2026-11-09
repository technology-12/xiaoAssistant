using System;
using System.Globalization;
using System.Linq;
using System.Net.Http.Headers;

namespace WorkAssistant.Infrastructure
{
    // Adapted from OpenCode SessionRetry: provider delay takes precedence over exponential backoff.
    public static class ModelRetryPolicy
    {
        public const int MaximumRetries = 2;
        public static bool IsRetryStatus(int status) => status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504 || status == 529;
        public static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(30);
        public static TimeSpan Delay(int retryNumber, TimeSpan? providerDelay)
        {
            var delay = providerDelay ?? TimeSpan.FromSeconds(Math.Pow(2, Math.Max(1, Math.Min(5, retryNumber))));
            return delay < TimeSpan.Zero ? TimeSpan.Zero : delay > MaximumDelay ? MaximumDelay : delay;
        }
        public static TimeSpan? ProviderDelay(HttpResponseHeaders headers)
        {
            if (headers.TryGetValues("retry-after-ms", out var values) &&
                double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) &&
                !double.IsNaN(milliseconds) && !double.IsInfinity(milliseconds) && milliseconds >= 0)
                return TimeSpan.FromMilliseconds(Math.Min(MaximumDelay.TotalMilliseconds, milliseconds));
            var retryAfter = headers.RetryAfter;
            if (retryAfter?.Delta != null) return retryAfter.Delta;
            if (retryAfter?.Date != null) return retryAfter.Date.Value - DateTimeOffset.UtcNow;
            // Some gateways send fractional seconds not recognized by RetryConditionHeaderValue.
            if (headers.TryGetValues("retry-after", out values) &&
                double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                !double.IsNaN(seconds) && !double.IsInfinity(seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(Math.Min(MaximumDelay.TotalSeconds, seconds));
            return null;
        }
    }
}
