using System;

namespace WorkAssistant.Infrastructure
{
    // Verified 2026-11-05: https://api-docs.deepseek.com/quick_start/pricing/
    // Only the official endpoint is matched. Compatible gateways keep their explicit catalog/settings.
    public static class ModelCapabilities
    {
        public static bool OfficialDeepSeek(ModelSettings model) => Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var uri) &&
            uri.Scheme == "https" && uri.Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
            (uri.AbsolutePath.TrimEnd('/') == "" || uri.AbsolutePath.TrimEnd('/') == "/v1" || uri.AbsolutePath.TrimEnd('/') == "/anthropic") &&
            (model.ModelName == "deepseek-flash" || model.ModelName == "deepseek-v4-pro");
        public static int DefaultOutput(ModelSettings model)
        {
            int selected = OfficialDeepSeek(model) ? 32768 : 8192;
            return model.ContextWindowTokens > 0 ? Math.Min(selected, model.ContextWindowTokens / 4) : selected;
        }
        public static void ApplyDefaults(ModelSettings model)
        {
            if (!OfficialDeepSeek(model)) return;
            if (model.ContextWindowTokens == 0) model.ContextWindowTokens = 1000000;
            // 32K is this application's bounded request budget, below the provider maximum.
            if (model.MaxOutputTokens == 0) model.MaxOutputTokens = DefaultOutput(model);
        }
    }
}
