using System;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Tools
{
    /// <summary>
    /// 工具基类：统一的参数解析、异常映射骨架。
    /// ToolException 携带稳定错误码；意外异常按各工具声明的兜底码映射。
    /// </summary>
    public abstract class AgentToolBase : IAgentTool
    {
        public abstract ToolDefinition Definition { get; }

        /// <summary>意外异常（非 ToolException）使用的兜底错误码。</summary>
        protected abstract string FallbackErrorCode { get; }

        public async Task<ToolResult> ExecuteAsync(ToolContext context, string argumentsJson,
            CancellationToken cancellationToken)
        {
            try
            {
                DocumentLimits.Begin(cancellationToken, Definition.Name);
                var args = ToolArgs.Parse(argumentsJson, Definition.Name);
                cancellationToken.ThrowIfCancellationRequested();
                return await ExecuteValidatedAsync(context, args, cancellationToken);
            }
            catch (ToolException ex)
            {
                return ToolResult.Failure(ex.Code, ex.Message);
            }
            catch (OperationCanceledException)
            {
                throw; // 取消信号交由控制器统一处理
            }
            catch (Exception ex)
            {
                return ToolResult.Failure(FallbackErrorCode,
                    "工具执行失败：" + ex.Message);
            }
        }

        protected abstract Task<ToolResult> ExecuteValidatedAsync(ToolContext context,
            JObject args, CancellationToken cancellationToken);

        protected static JObject Schema(params (string name, string type, string description, bool required)[] props)
        {
            var properties = new JObject();
            var required = new JArray();
            foreach (var (name, type, description, req) in props)
            {
                properties[name] = new JObject
                {
                    ["type"] = type,
                    ["description"] = description
                };
                if (req) required.Add(name);
            }
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false
            };
            if (required.Count > 0) schema["required"] = required;
            return schema;
        }
    }
}
