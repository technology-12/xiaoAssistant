using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    // Reads OpenCode-style provider/models/limit metadata; never executes npm modules or imports credentials.
    public sealed class WorkspaceModelCatalog
    {
        private readonly JObject? _providers;
        public WorkspaceModelCatalog(string workspace)
        {
            var root = Path.Combine(workspace, ".claude"); var path = Path.Combine(root, "opencode.models.json");
            if (!File.Exists(path)) return;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 256 * 1024)
                throw new ToolException(ErrorCodes.InvalidArguments, "工作区模型目录路径或大小无效。");
            try
            {
                using var reader = new JsonTextReader(new StreamReader(path)) { MaxDepth = 24, CloseInput = true };
                var obj = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                _providers = obj["provider"] as JObject ?? throw new ToolException(ErrorCodes.InvalidArguments, "模型目录需要 provider 对象。");
            }
            catch (JsonException) { throw new ToolException(ErrorCodes.InvalidArguments, "工作区模型目录不是有效 JSON。"); }
        }
        public void Apply(ModelSettings settings)
        {
            if (_providers == null) return;
            try
            {
                JObject? selected = null;
                foreach (var provider in _providers.Properties().Select(p => p.Value).OfType<JObject>())
                {
                    if (EndpointKey((string?)provider["options"]?["baseURL"] ?? "") != EndpointKey(settings.BaseUrl)) continue;
                    if (provider["models"] is not JObject models) continue;
                    foreach (var model in models.Properties())
                    {
                        if (model.Value is not JObject entry || model.Name != settings.ModelName && (string?)entry["id"] != settings.ModelName) continue;
                        if (selected != null) throw new ToolException(ErrorCodes.InvalidArguments, "模型目录中存在多个相同端点和模型的定义。");
                        selected = entry;
                    }
                }
                if (selected?["limit"] is not JObject limit) return;
                if (settings.ContextWindowTokens == 0) settings.ContextWindowTokens = Read(limit, "context");
                if (settings.MaxOutputTokens == 0) settings.MaxOutputTokens = Read(limit, "output");
                ModelClientFactory.Validate(settings);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            { throw new ToolException(ErrorCodes.InvalidArguments, "模型目录字段类型或限制无效。"); }
        }
        private static int Read(JObject obj, string name)
        {
            if (obj[name] == null) return 0;
            if (obj[name]!.Type != JTokenType.Integer) throw new ToolException(ErrorCodes.InvalidArguments, "模型限制必须为整数。");
            return obj[name]!.Value<int>();
        }
        private static string EndpointKey(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "";
            return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped).ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/');
        }
    }
}
