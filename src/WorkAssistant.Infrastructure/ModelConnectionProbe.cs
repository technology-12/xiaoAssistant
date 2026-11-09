using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    public static class ModelConnectionProbe
    {
        public static async Task<ConnectionTestResult> TestAsync(ModelSettings settings, CancellationToken token)
        {
            using var owner = new ModelOwner(ModelClientFactory.Create(settings, capabilityProbe: true));
            return await TestClientAsync(owner.Client, settings.ModelName, token).ConfigureAwait(false);
        }
        public static async Task<ConnectionTestResult> TestClientAsync(IModelClient client, string model, CancellationToken token)
        {
            var probe = new ToolDefinition("ping_probe", "连接能力探测", JObject.Parse(@"{""type"":""object"",""properties"":{""nonce"":{""type"":""string""}},""required"":[""nonce""],""additionalProperties"":false}"));
            var request = new ModelRequest(model, new[] { ChatMessage.User("连接测试：必须调用 ping_probe，并设置 nonce 为 connection-test。") }, new[] { probe }, "required", 256);
            bool Valid(ModelReply value)
            {
                try { return value.ToolCalls?.Count == 1 && value.ToolCalls[0].Name == probe.Name && JObject.Parse(value.ToolCalls[0].ArgumentsJson).Value<string>("nonce") == "connection-test"; }
                catch (Newtonsoft.Json.JsonException) { return false; }
            }
            try
            {
                var reply = await client.CompleteAsync(request, token).ConfigureAwait(false);
                bool valid = Valid(reply); bool? streaming = null;
                if (valid && client is IStreamingModelClient streamed)
                {
                    try { var result = await streamed.CompleteStreamAsync(request, _ => { }, token).ConfigureAwait(false); if (Valid(result)) streaming = result.Streamed; }
                    catch (ModelHttpException ex) when (ex.Category == "stream-unsupported") { streaming = false; }
                    catch (ToolException) { /* A transient or malformed response is not proof of missing capability. */ }
                }
                return new ConnectionTestResult(true, true, valid, valid ? "已校验工具名称和参数。" : "模型未返回有效工具调用，能力尚未确认。", streamingSupported: streaming, toolCallingChecked: valid);
            }
            catch (ModelHttpException ex) when (ex.Category == "tools-unsupported" && (ex.StatusCode == 400 || ex.StatusCode == 415 || ex.StatusCode == 422))
            {
                try
                {
                    await client.CompleteAsync(new ModelRequest(model, new[] { ChatMessage.User("仅回复 ok。") }, new ToolDefinition[0], maxOutputTokens: 32), token).ConfigureAwait(false);
                    return new ConnectionTestResult(true, true, false, "模型可连接，但服务明确不支持工具调用。", toolCallingChecked: true);
                }
                catch (ToolException) { return new ConnectionTestResult(false, false, false, "接口未完成基础响应验证。", authChecked: false, toolCallingChecked: false); }
            }
            catch (ToolException ex) when (ex.Code == ErrorCodes.ModelAuthFailed)
            { return new ConnectionTestResult(false, false, false, "认证失败，请检查密钥。", toolCallingChecked: false); }
            catch (ToolException)
            { return new ConnectionTestResult(false, false, false, "接口暂不可用或响应异常；未将其缓存为不支持。", authChecked: false, toolCallingChecked: false); }
        }
        private sealed class ModelOwner : IDisposable
        {
            public IModelClient Client { get; }
            public ModelOwner(IModelClient client) { Client = client; }
            public void Dispose() => (Client as IDisposable)?.Dispose();
        }
    }
}
