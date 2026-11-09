using System.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    /// <summary>连接测试分级结果（设计 §6.2）。</summary>
    public sealed class ConnectionTestResult
    {
        public bool AuthOk { get; }
        public bool ModelOk { get; }
        public bool ToolCallingOk { get; }
        public string Message { get; }
        public bool AuthChecked { get; }
        public bool? StreamingSupported { get; }
        public bool ToolCallingChecked { get; }

        public ConnectionTestResult(bool authOk, bool modelOk, bool toolCallingOk, string message, bool authChecked = true, bool? streamingSupported = null, bool toolCallingChecked = true)
        {
            AuthOk = authOk;
            ModelOk = modelOk;
            ToolCallingOk = toolCallingOk;
            Message = message;
            AuthChecked = authChecked; StreamingSupported = streamingSupported;
            ToolCallingChecked = toolCallingChecked;
        }
        public ConnectionTestResult(bool authOk, bool modelOk, bool toolCallingOk, string message, bool authChecked, bool? streamingSupported)
            : this(authOk, modelOk, toolCallingOk, message, authChecked, streamingSupported, true) { }
    }

    /// <summary>
    /// OpenAI Chat Completions 兼容客户端（设计 §8 + 正式版 §7.2）：
    /// - 非流式与 SSE 流式；Bearer 认证；TLS 1.2 显式启用（Win7 兼容）。
    /// - 流式按调用索引/ID 缓存工具参数片段，收到完整结束信号后才组装 ToolCall（不执行半截 JSON）。
    /// - 断流视为 MODEL_UNAVAILABLE（可重试），不把部分文字判为成功。
    /// - 连接失败/408/429/500/502/503/504 有限重试（最多额外 2 次），优先读取 retry-after-ms，等待可取消。
    /// - 401/403、无效模型、格式错误不重试，直接映射稳定错误码。
    /// </summary>
    public sealed class HttpModelClient : IStreamingModelClient, IModelRetrySource, IDisposable
    {
        private const int MaxRetries = ModelRetryPolicy.MaximumRetries;
        public event Action<ModelRetryInfo>? Retrying;

        private readonly Uri _endpoint;
        private readonly string _apiKey;
        private readonly string _modelName;
        private readonly TimeSpan _timeout;
        private readonly bool _useSystemProxy;
        private readonly HttpClient _client;
        private readonly bool _preferStreaming;
        private volatile bool _streamingRejected;
        private readonly ModelSettings _settings;

        static HttpModelClient()
        {
            // Win7 默认可能不含 TLS 1.2，显式启用（不关闭系统其余默认值）。
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        }

        public HttpModelClient(ModelSettings settings)
        {
            ModelClientFactory.Validate(settings); _settings = settings;
            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            {
                throw new ToolException(ErrorCodes.InvalidArguments, "base_url 未配置。");
            }
            if (string.IsNullOrWhiteSpace(settings.ModelName))
            {
                throw new ToolException(ErrorCodes.InvalidArguments, "model 未配置。");
            }
            _endpoint = BuildEndpoint(settings.BaseUrl);
            _apiKey = settings.ApiKey ?? "";
            _modelName = settings.ModelName;
            _timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds <= 0 ? 90 : settings.TimeoutSeconds);
            _useSystemProxy = settings.UseSystemProxy;
            _preferStreaming = settings.StreamingSupported != false;

            var handler = new HttpClientHandler();
            if (!_useSystemProxy)
            {
                handler.Proxy = null;
                handler.UseProxy = false;
            }
            // 证书校验保持默认开启，不实现全局跳过（设计 §6.2）。
            _client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(-1) // 无限超时（-1ms，等价系统无限值）；每请求用cts控制
            };
            _client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        }

        /// <summary>规范化 URL 合并：去尾部 '/'，追加 /chat/completions，不重复添加 /v1。</summary>
        internal static Uri BuildEndpoint(string baseUrl)
        {
            var trimmed = baseUrl.Trim().TrimEnd('/');
            if (!trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(ErrorCodes.InvalidArguments,
                    "base_url 必须以 https:// 或 http:// 开头。");
            }
            return new Uri(trimmed + "/chat/completions", UriKind.Absolute);
        }

        public async Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            request = ModelClientFactory.WithOutputLimit(request, _settings);
            new ModelContextBudget(_settings.ContextWindowTokens, request.MaxOutputTokens).Demand(request);
            var payload = SerializeRequest(request);
            var attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    return await SendOnceAsync(payload, cancellationToken);
                }
                catch (ModelHttpException ex) when (attempt <= MaxRetries && IsRetryable(ex))
                {
                    var delay = ModelRetryPolicy.Delay(attempt, ex.RetryAfter);
                    Retrying?.Invoke(new ModelRetryInfo(attempt, ex.StatusCode, delay));
                    await XpTask.Delay(delay, cancellationToken);
                }
            }
        }

        // ---- SSE 流式（正式版 §7.2）------------------------------------------

        /// <summary>
        /// 流式补全：text 增量实时回调；工具调用片段按 index 聚合，
        /// [DONE]/finish_reason 后才组装完整 ToolCall。断流抛 MODEL_UNAVAILABLE。
        /// </summary>
        public async Task<ModelReply> CompleteStreamAsync(ModelRequest request,
            Action<string>? onTextDelta, CancellationToken cancellationToken)
        {
            if (!_preferStreaming || _streamingRejected) return await CompleteAsync(request, cancellationToken);
            request = ModelClientFactory.WithOutputLimit(request, _settings);
            new ModelContextBudget(_settings.ContextWindowTokens, request.MaxOutputTokens).Demand(request);
            var payload = SerializeRequest(request, stream: true);
            var attempt = 0;
            while (true)
            {
                attempt++;
                var emittedAny = false;
                try
                {
                    var response = await SendStreamOnceAsync(payload, delta =>
                    {
                        emittedAny = true;
                        onTextDelta?.Invoke(delta);
                    }, cancellationToken);
                    if (!response.Streamed) _streamingRejected = true;
                    return response;
                }
                catch (ModelHttpException ex) when (!emittedAny && ex.Category == "stream-unsupported" && (ex.StatusCode == 400 || ex.StatusCode == 415 || ex.StatusCode == 422))
                { _streamingRejected = true; return await CompleteAsync(request, cancellationToken).ConfigureAwait(false); }
                catch (ModelHttpException ex) when (attempt <= MaxRetries && IsRetryable(ex) && !emittedAny)
                {
                    // 已向用户输出过文字的流不重试，避免重复内容。
                    var delay = ModelRetryPolicy.Delay(attempt, ex.RetryAfter);
                    Retrying?.Invoke(new ModelRetryInfo(attempt, ex.StatusCode, delay));
                    await XpTask.Delay(delay, cancellationToken);
                }
            }
        }

        private async Task<ModelReply> SendStreamOnceAsync(string payload,
            Action<string> onTextDelta, CancellationToken cancellationToken)
        {
            HttpResponseMessage? response = null;
            Stream? bodyStream = null;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            try
            {
                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                try
                {
                    using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = content };
                    response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ModelHttpException(ErrorCodes.ModelUnavailable,
                        $"模型请求超过 {_timeout.TotalSeconds:0} 秒超时。", null);
                }
                catch (HttpRequestException ex)
                {
                    throw new ModelHttpException(ErrorCodes.ModelUnavailable,
                        "无法连接模型服务：" + ex.Message, null);
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await ReadBodyAsync(response.Content, cts.Token);
                    throw new ModelHttpException(MapStatus(response.StatusCode, errorBody),
                        $"HTTP {(int)response.StatusCode}：{SummarizeBody(errorBody)}",
                        response.Headers.RetryAfter, (int)response.StatusCode, ModelRetryPolicy.ProviderDelay(response.Headers), ModelServiceError.ForStatus((int)response.StatusCode, errorBody));
                }
                if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                {
                    // 端点不支持流式（例如部分网关忽略 stream 参数）：退回非流式解析。
                    var body = await ReadBodyAsync(response.Content, cts.Token);
                    return ParseReply(body);
                }
                // 响应在流读取完毕前保持存活（finally 统一释放）。
                bodyStream = new LimitedReadStream(await response.Content.ReadAsStreamAsync(), ModelResponseLimits.StreamBytes);

                return await ParseSseStreamAsync(bodyStream, onTextDelta, cts.Token);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && IsReadCancellation(ex))
            { throw new OperationCanceledException("Model request cancelled.", ex, cancellationToken); }
            catch (Exception ex) when (cts.IsCancellationRequested && IsReadCancellation(ex))
            { throw new ModelHttpException(ErrorCodes.ModelUnavailable, "读取模型响应超时。", null); }
            catch (InvalidDataException)
            { throw new ModelHttpException(ErrorCodes.ModelResponseTooLarge, "模型流式传输超过 64 MiB，请分批生成；未执行不完整工具调用。", null); }
            catch (IOException ex)
            {
                throw new ModelHttpException(ErrorCodes.ModelUnavailable,
                    "流式响应中断：" + ex.Message, null);
            }
            finally
            {
                bodyStream?.Dispose();
                response?.Dispose();
            }
        }

        /// <summary>SSE 解析：data: {...} 行；[DONE] 结束；delta.content / delta.tool_calls 片段聚合。</summary>
        private static async Task<ModelReply> ParseSseStreamAsync(
            System.IO.Stream stream, Action<string> onTextDelta, CancellationToken cancellationToken)
        {
            var content = new System.Text.StringBuilder(); var reasoning = new System.Text.StringBuilder();
            string? finishReason = null;
            ModelUsage? usage = null;
            // index -> (id, name, argsBuilder)
            var calls = new SortedDictionary<int, (string? Id, string? Name, System.Text.StringBuilder Args)>();
            var sawDone = false;

            using var reader = new System.IO.StreamReader(stream,
                System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            // 取消时关闭底层流，解除阻塞的 ReadLineAsync。
            using var registration = cancellationToken.Register(() =>
            {
                try { stream.Close(); } catch (ObjectDisposedException) { }
            });

            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (line.Length == 0) continue;
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
                var data = line.Substring(5).Trim();
                if (data == "[DONE]")
                {
                    sawDone = true;
                    break;
                }
                JObject chunk;
                try
                {
                    chunk = JObject.Parse(data);
                }
                catch
                {
                    throw new ModelHttpException(ErrorCodes.ModelProtocolError,
                        "SSE 数据块不是合法 JSON：" + SummarizeBody(data), null);
                }

                var error = chunk["error"];
                if (error != null)
                {
                    throw new ModelHttpException(ErrorCodes.ModelProtocolError,
                        "服务返回错误：" + SummarizeBody(error.ToString(Newtonsoft.Json.Formatting.None)), null);
                }

                var usageToken = chunk["usage"];
                if (usageToken is { Type: JTokenType.Object })
                {
                    usage = new ModelUsage(
                        (int?)usageToken["prompt_tokens"] ?? 0,
                        (int?)usageToken["completion_tokens"] ?? 0, (long?)usageToken["completion_tokens_details"]?["reasoning_tokens"] ?? 0);
                }

                var choice = chunk["choices"]?[0];
                if (choice == null) continue;

                var finish = (string?)choice["finish_reason"];
                if (finish != null) finishReason = finish;

                var delta = choice["delta"];
                if (delta == null) continue;

                var reasoningDelta = (string?)delta["reasoning_content"];
                if (reasoningDelta != null) { ModelResponseLimits.DemandText(reasoning.Length + reasoningDelta.Length); reasoning.Append(reasoningDelta); }
                var deltaContent = (string?)delta["content"];
                if (deltaContent != null && deltaContent.Length > 0)
                {
                    ModelResponseLimits.DemandText(content.Length + deltaContent.Length);
                    content.Append(deltaContent);
                    onTextDelta(deltaContent);
                }

                var toolFragments = delta["tool_calls"] as JArray;
                if (toolFragments != null)
                {
                    foreach (var fragment in toolFragments)
                    {
                        var index = (int?)fragment["index"] ?? calls.Count;
                        if (index < 0 || index >= AgentController.MaxToolCalls) throw new ModelHttpException(ErrorCodes.ModelProtocolError, "流式工具数量超限。", null);
                        if (!calls.TryGetValue(index, out var slot))
                        {
                            slot = (null, null, new System.Text.StringBuilder());
                            calls[index] = slot;
                        }
                        var id = (string?)fragment["id"];
                        if (id != null) calls[index] = (calls[index].Id + id, calls[index].Name, calls[index].Args);
                        var fn = fragment["function"];
                        if (fn != null)
                        {
                            var name = (string?)fn["name"];
                            if (name != null) calls[index] = (calls[index].Id, calls[index].Name + name, calls[index].Args);
                            var argsChunk = (string?)fn["arguments"];
                            if (argsChunk != null)
                            {
                                calls[index].Args.Append(argsChunk);
                                ModelResponseLimits.DemandArguments(calls[index].Args.Length);
                            }
                        }
                    }
                }
            }

            if (!sawDone && finishReason == null)
            {
                throw new ModelHttpException(ErrorCodes.ModelUnavailable,
                    "流式响应未正常结束（可能被网络中断）。", null);
            }

            if (finishReason == "length") throw new ModelOutputTruncatedException(usage, content.ToString(), string.Join("\n", calls.Values.Select(c => c.Name + ": " + c.Args.ToString())), reasoning.Length == 0 ? null : reasoning.ToString());
            List<ToolCall>? toolCalls = null;
            if (calls.Count > 0)
            {
                toolCalls = new List<ToolCall>();
                foreach (var entry in calls)
                {
                    var (id, name, args) = entry.Value;
                    if (id == null || name == null)
                    {
                        throw new ModelHttpException(ErrorCodes.ModelProtocolError,
                            $"流式工具调用片段不完整（index={entry.Key} 缺少 id 或名称）。", null);
                    }
                    toolCalls.Add(new ToolCall(id, name, args.ToString()));
                }
            }

            
            if (finishReason != "stop" && finishReason != "tool_calls")
                throw new ModelHttpException(ErrorCodes.ModelProtocolError, "模型未完整结束：" + (finishReason ?? "缺少 finish_reason"), null);
            return new ModelReply(content.Length == 0 ? null : content.ToString(), toolCalls, finishReason, streamed: true, usage, reasoningContent: reasoning.Length == 0 ? null : reasoning.ToString());
        }

        private async Task<ModelReply> SendOnceAsync(string payload, CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);
            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                { Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json") };
                using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                string body = await ReadBodyAsync(response.Content, cts.Token);
                if (!response.IsSuccessStatusCode)
                    throw new ModelHttpException(MapStatus(response.StatusCode, body),
                        $"HTTP {(int)response.StatusCode}：{SummarizeBody(body)}", response.Headers.RetryAfter, (int)response.StatusCode, ModelRetryPolicy.ProviderDelay(response.Headers), ModelServiceError.ForStatus((int)response.StatusCode, body));
                return ParseReply(body);
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && IsReadCancellation(ex))
            { throw new OperationCanceledException("Model request cancelled.", ex, cancellationToken); }
            catch (Exception ex) when (cts.IsCancellationRequested && IsReadCancellation(ex))
            { throw new ModelHttpException(ErrorCodes.ModelUnavailable, $"模型请求超过 {_timeout.TotalSeconds:0} 秒超时。", null); }
            catch (HttpRequestException ex)
            { throw new ModelHttpException(ErrorCodes.ModelUnavailable, "无法连接模型服务：" + ex.Message, null); }
            catch (InvalidDataException)
            { throw new ModelHttpException(ErrorCodes.ModelResponseTooLarge, "模型响应超过 16 MiB，请分批生成；未执行不完整工具调用。", null); }
            catch (System.Text.DecoderFallbackException)
            { throw new ModelHttpException(ErrorCodes.ModelProtocolError, "模型响应不是有效 UTF-8。", null); }
            catch (IOException)
            { throw new ModelHttpException(ErrorCodes.ModelUnavailable, "模型响应读取中断。", null); }
        }

        private static bool IsReadCancellation(Exception ex) => ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException;

        private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken token)
        {
            using var stream = new LimitedReadStream(await content.ReadAsStreamAsync(), ModelResponseLimits.JsonBytes);
            using var registration = token.Register(() => stream.Dispose());
            using var buffer = new System.IO.MemoryStream();
            await stream.CopyToAsync(buffer, 32768, token);
            return new System.Text.UTF8Encoding(false, true).GetString(buffer.ToArray());
        }

        private static string MapStatus(HttpStatusCode status, string body)
        {
            if (status == HttpStatusCode.Unauthorized || status == HttpStatusCode.Forbidden) return ErrorCodes.ModelAuthFailed;
            var category = ModelServiceError.ForStatus((int)status, body);
            if (category == "context-overflow") return ErrorCodes.ContextLengthExceeded;
            if (category == "tools-unsupported") return ErrorCodes.ToolCallingUnsupported;
            switch ((int)status)
            {
                case 401:
                case 403:
                    return ErrorCodes.ModelAuthFailed;
                case 408:
                case 429:
                case 500:
                case 502:
                case 503:
                case 504:
                case 529:
                    return ErrorCodes.ModelUnavailable;
                default:
                    // 400/404 常见于无效模型或网关路径错误。
                    return ErrorCodes.ModelProtocolError;
            }
        }

        private static string SummarizeBody(string body)
        {
            // Provider error bodies can echo prompts or credentials; keep diagnostics structural.
            return string.IsNullOrWhiteSpace(body) ? "服务返回空响应" : "服务响应格式或状态异常（正文未写入诊断）";
        }

        internal static ModelReply ParseReply(string body)
        {
            JObject root;
            try
            {
                root = JObject.Parse(body);
            }
            catch
            {
                throw new ModelHttpException(ErrorCodes.ModelProtocolError,
                    "响应不是合法 JSON：" + SummarizeBody(body), null);
            }

            var error = root["error"];
            if (error != null)
            {
                throw new ModelHttpException(ErrorCodes.ModelProtocolError,
                    "服务返回错误：" + SummarizeBody(error.ToString(Newtonsoft.Json.Formatting.None)), null);
            }

            var message = root["choices"]?[0]?["message"];
            if (message == null)
            {
                throw new ModelHttpException(ErrorCodes.ModelProtocolError,
                    "响应缺少 choices[0].message。", null);
            }

            string? content = null;
            if (message["content"] != null && message["content"]!.Type != JTokenType.Null)
            {
                if (message["content"]!.Type != JTokenType.String)
                {
                    throw new ModelHttpException(ErrorCodes.ModelProtocolError,
                        "message.content 类型异常（期望字符串）。", null);
                }
                content = (string?)message["content"];
                ModelResponseLimits.DemandText(content?.Length ?? 0);
            }

            if ((string?)root["choices"]?[0]?["finish_reason"] == "length")
            {
                var cutUsage = root["usage"] as JObject;
                throw new ModelOutputTruncatedException(cutUsage == null ? null : new ModelUsage((long?)cutUsage["prompt_tokens"] ?? 0, (long?)cutUsage["completion_tokens"] ?? 0, (long?)cutUsage["completion_tokens_details"]?["reasoning_tokens"] ?? 0), content, message["tool_calls"]?.ToString(Newtonsoft.Json.Formatting.None), (string?)message["reasoning_content"]);
            }
            List<ToolCall>? toolCalls = null;
            var rawCalls = message["tool_calls"] as JArray;
            if (rawCalls != null)
            {
                toolCalls = new List<ToolCall>();
                foreach (var raw in rawCalls)
                {
                    var id = (string?)raw["id"];
                    var fn = raw["function"];
                    var name = (string?)fn?["name"];
                    var args = (string?)fn?["arguments"];
                    if (id == null || name == null || args == null)
                    {
                        throw new ModelHttpException(ErrorCodes.ModelProtocolError,
                            "tool_calls 结构不完整（缺少 id/function.name/function.arguments）。", null);
                    }
                    ModelResponseLimits.DemandArguments(args.Length);
                    toolCalls.Add(new ToolCall(id, name, args));
                }
            }

            var finish = (string?)root["choices"]?[0]?["finish_reason"] ?? "unknown";
            string? reasoningText = (string?)message["reasoning_content"]; ModelResponseLimits.DemandText(reasoningText?.Length ?? 0);
            var usage = root["usage"] as JObject;
            if (finish == "length") throw new ModelOutputTruncatedException(usage == null ? null : new ModelUsage((long?)usage["prompt_tokens"] ?? 0, (long?)usage["completion_tokens"] ?? 0), content, string.Join("\n", toolCalls?.Select(c => c.Name + ": " + c.ArgumentsJson) ?? Enumerable.Empty<string>()), reasoningText);
            if (finish != "stop" && finish != "tool_calls")
                throw new ModelHttpException(ErrorCodes.ModelProtocolError, "模型未完整结束：" + finish, null);
            return new ModelReply(content, toolCalls, finish, usage: usage == null ? null : new ModelUsage(
                (long?)usage["prompt_tokens"] ?? 0, (long?)usage["completion_tokens"] ?? 0, (long?)usage["completion_tokens_details"]?["reasoning_tokens"] ?? 0), reasoningContent: reasoningText);
        }

        internal static string SerializeRequest(ModelRequest request) =>
            SerializeRequest(request, stream: false);

        internal static string SerializeRequest(ModelRequest request, bool stream) => ModelRequestJson.Serialize(request, stream);

        public void Dispose() => _client.Dispose();

        // ---- 重试策略 -------------------------------------------------------

        private static bool IsRetryable(ModelHttpException ex)
        {
            if (ex.Code != ErrorCodes.ModelUnavailable) return false;
            return true;
        }

        /// <summary>测试连接：分级报告认证、模型可用性与工具调用能力。</summary>
        public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken) =>
            ModelConnectionProbe.TestAsync(_settings, cancellationToken);

    }

    /// <summary>携带错误码与可选 Retry-After 的 HTTP 异常；继承 ToolException 以便控制器按码处理。</summary>
    public sealed class ModelHttpException : ToolException
    {
        public string Category { get; }
        public TimeSpan? RetryAfter { get; }
        public int? StatusCode { get; }

        public ModelHttpException(string code, string message,
            System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter, int? statusCode = null, TimeSpan? providerDelay = null, string category = "")
            : base(code, message)
        {
            StatusCode = statusCode;
            Category = category;
            if (providerDelay.HasValue) RetryAfter = providerDelay;
            else if (retryAfter != null)
            {
                if (retryAfter.Delta.HasValue) RetryAfter = retryAfter.Delta;
                else if (retryAfter.Date.HasValue)
                {
                    RetryAfter = retryAfter.Date.Value.UtcDateTime - DateTime.UtcNow;
                }
            }
        }
        public ModelHttpException(string code, string message, System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter, int? statusCode, TimeSpan? providerDelay)
            : this(code, message, retryAfter, statusCode, providerDelay, "") { }
        public ModelHttpException(string code, string message, System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter, int? statusCode)
            : this(code, message, retryAfter, statusCode, null, "") { }
    }
}
