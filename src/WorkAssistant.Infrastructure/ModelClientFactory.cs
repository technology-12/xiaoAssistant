using System;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    public static class ModelClientFactory
    {
        public static IModelClient Create(ModelSettings settings, bool capabilityProbe = false)
        {
            if (capabilityProbe) settings = new ModelSettings { BaseUrl = settings.BaseUrl, ApiKey = settings.ApiKey, ModelName = settings.ModelName,
                Protocol = settings.Protocol, AnthropicVersion = settings.AnthropicVersion, ContextWindowTokens = settings.ContextWindowTokens,
                MaxOutputTokens = settings.MaxOutputTokens, TimeoutSeconds = settings.TimeoutSeconds, UseSystemProxy = settings.UseSystemProxy };
            ModelCapabilities.ApplyDefaults(settings);
            Validate(settings);
            return settings.Protocol == "anthropic-messages" ? new AnthropicModelClient(settings) : new HttpModelClient(settings);
        }
        public static void Validate(ModelSettings settings)
        {
            if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" && endpoint.Scheme != "http" ||
                endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || string.IsNullOrWhiteSpace(settings.ModelName) ||
                (settings.ApiKey ?? "").IndexOfAny(new[] { '\r', '\n' }) >= 0)
                throw new ToolException(ErrorCodes.InvalidArguments, "模型地址、名称或认证配置无效；地址不能包含用户名、查询或片段。");
            if (settings.Protocol != "openai-chat" && settings.Protocol != "anthropic-messages")
                throw new ToolException(ErrorCodes.InvalidArguments, "模型协议不受支持，请选择 Chat Completions 或 Anthropic Messages。");
            _ = new ModelContextBudget(settings.ContextWindowTokens, settings.EffectiveOutputTokens);
            if (settings.Protocol == "anthropic-messages" && !System.Text.RegularExpressions.Regex.IsMatch(settings.AnthropicVersion ?? "", @"\A\d{4}-\d{2}-\d{2}\z"))
                throw new ToolException(ErrorCodes.InvalidArguments, "Anthropic API 版本必须为 YYYY-MM-DD。");
        }
        public static ModelRequest WithOutputLimit(ModelRequest request, ModelSettings settings)
        {
            int limit = request.MaxOutputTokens;
            if (limit == 0) limit = settings.EffectiveOutputTokens;
            else if (settings.EffectiveOutputTokens > 0) limit = Math.Min(limit, settings.EffectiveOutputTokens);
            return new ModelRequest(request.Model, request.Messages, request.Tools, request.ToolChoice, limit);
        }
    }
}
