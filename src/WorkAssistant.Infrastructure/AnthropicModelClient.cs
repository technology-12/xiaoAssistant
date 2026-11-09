using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WorkAssistant.Core;

namespace WorkAssistant.Infrastructure
{
    public sealed class AnthropicModelClient : IStreamingModelClient, IModelRetrySource, IDisposable
    {
        private readonly ModelSettings _settings;
        private readonly HttpClient _client;
        private readonly Uri _endpoint;
        private volatile bool _streamingRejected;
        public event Action<ModelRetryInfo>? Retrying;
        public AnthropicModelClient(ModelSettings settings)
        {
            ModelClientFactory.Validate(settings); _settings = settings;
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            _endpoint = Endpoint(settings.BaseUrl);
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = settings.UseSystemProxy };
            if (!settings.UseSystemProxy) handler.Proxy = null;
            _client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(-1) };
            _client.DefaultRequestHeaders.Add("x-api-key", settings.ApiKey);
            _client.DefaultRequestHeaders.Add("anthropic-version", settings.AnthropicVersion);
        }
        public static Uri Endpoint(string baseUrl)
        {
            var url = baseUrl.Trim().TrimEnd('/');
            if (!url.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)) url += url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/messages" : "/v1/messages";
            return new Uri(url, UriKind.Absolute);
        }
        public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken token) => Complete(request, false, null, token);
        public Task<ModelReply> CompleteStreamAsync(ModelRequest request, Action<string>? delta, CancellationToken token) =>
            Complete(request, _settings.StreamingSupported != false && !_streamingRejected, delta, token);
        private async Task<ModelReply> Complete(ModelRequest request, bool stream, Action<string>? delta, CancellationToken token)
        {
            request = ModelClientFactory.WithOutputLimit(request, _settings);
            new ModelContextBudget(_settings.ContextWindowTokens, request.MaxOutputTokens).Demand(request);
            var payload = AnthropicMessageCodec.Serialize(request, stream);
            for (int attempt = 0; ; attempt++)
            {
                bool emitted = false;
                try
                {
                    var reply = await Send(payload, stream, part => { emitted = true; delta?.Invoke(part); }, token).ConfigureAwait(false);
                    if (stream && !reply.Streamed) _streamingRejected = true;
                    return reply;
                }
                catch (ModelHttpException ex) when (stream && !emitted && ex.Category == "stream-unsupported" && (ex.StatusCode == 400 || ex.StatusCode == 415 || ex.StatusCode == 422))
                { _streamingRejected = true; return await Complete(request, false, null, token).ConfigureAwait(false); }
                catch (ModelHttpException ex) when (!emitted && attempt < ModelRetryPolicy.MaximumRetries && ex.Code == ErrorCodes.ModelUnavailable)
                {
                    var delay = ModelRetryPolicy.Delay(attempt + 1, ex.RetryAfter);
                    Retrying?.Invoke(new ModelRetryInfo(attempt + 1, ex.StatusCode, delay));
                    await XpTask.Delay(delay, token).ConfigureAwait(false);
                }
            }
        }
        private async Task<ModelReply> Send(string payload, bool stream, Action<string> delta, CancellationToken token)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds <= 0 ? 90 : _settings.TimeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                bool eventStream = response.Content.Headers.ContentType?.MediaType == "text/event-stream";
                using var body = new LimitedReadStream(await response.Content.ReadAsStreamAsync().ConfigureAwait(false), eventStream ? ModelResponseLimits.StreamBytes : ModelResponseLimits.JsonBytes);
                using var registration = timeout.Token.Register(() => { try { body.Dispose(); } catch (ObjectDisposedException) { } });
                if (response.IsSuccessStatusCode && stream && response.Content.Headers.ContentType?.MediaType == "text/event-stream")
                    return await AnthropicMessageCodec.ParseStreamAsync(body, delta, timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream(); await body.CopyToAsync(buffer, 32768, timeout.Token).ConfigureAwait(false);
                var text = new UTF8Encoding(false, true).GetString(buffer.ToArray());
                if (!response.IsSuccessStatusCode)
                {
                    var status = (int)response.StatusCode; var category = ModelServiceError.ForStatus(status, text);
                    var code = status == 401 || status == 403 ? ErrorCodes.ModelAuthFailed : category == "context-overflow" ? ErrorCodes.ContextLengthExceeded :
                        category == "tools-unsupported" ? ErrorCodes.ToolCallingUnsupported : ModelRetryPolicy.IsRetryStatus(status) ? ErrorCodes.ModelUnavailable : ErrorCodes.ModelProtocolError;
                    throw new ModelHttpException(code, "Messages HTTP " + status + "；响应正文未写入诊断。", response.Headers.RetryAfter, status, ModelRetryPolicy.ProviderDelay(response.Headers), category);
                }
                return AnthropicMessageCodec.Parse(text);
            }
            catch (Exception ex) when (token.IsCancellationRequested && IsCancellation(ex)) { throw new OperationCanceledException("模型请求已取消。", ex, token); }
            catch (Exception ex) when (timeout.IsCancellationRequested && IsCancellation(ex)) { throw new ModelHttpException(ErrorCodes.ModelUnavailable, "Messages 请求超时。", null); }
            catch (InvalidDataException) { throw new ToolException(ErrorCodes.ModelResponseTooLarge, "模型传输超过安全上限（JSON 16 MiB、流式 64 MiB），请分批生成。"); }
            catch (HttpRequestException) { throw new ModelHttpException(ErrorCodes.ModelUnavailable, "无法连接 Messages 服务。", null); }
            catch (IOException) { throw new ModelHttpException(ErrorCodes.ModelUnavailable, "Messages 读取中断。", null); }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is ArgumentException || ex is InvalidCastException || ex is FormatException || ex is OverflowException)
            { throw new ToolException(ErrorCodes.ModelProtocolError, "Messages 响应字段不完整或类型无效。"); }
        }
        private static bool IsCancellation(Exception ex) => ex is OperationCanceledException || ex is IOException || ex is ObjectDisposedException;
        public void Dispose() => _client.Dispose();
    }
}
