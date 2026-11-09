// System.Net.Http 的 net40 垫片：命名空间兼容的 HttpClient 家族，基于 HttpWebRequest。
// 共享源码（HttpModelClient/AnthropicModelClient/OpenCodeGatewayClient/ModelRetryPolicy）
// 无需修改即可编译，获得基线 §4 的“可注入的 XP 传输层”。
// - 流式：AllowReadStreamBuffering=false，SSE 按行增量到达。
// - 取消：token 触发 request.Abort()，读取方将 IOException/WebException 归并为取消。
// - 非 2xx 状态按 HttpResponseMessage 返回（与 HttpClient 语义一致），传输错误映射
//   HttpRequestException；TLS 能力取决于 XP Schannel（见基线联网方案，不在垫片内伪装）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    public enum HttpCompletionOption { ResponseContentRead, ResponseHeadersRead }

    public sealed class HttpMethod
    {
        public static readonly HttpMethod Get = new HttpMethod("GET");
        public static readonly HttpMethod Post = new HttpMethod("POST");
        public static readonly HttpMethod Put = new HttpMethod("PUT");
        public static readonly HttpMethod Delete = new HttpMethod("DELETE");
        public string Method { get; }
        public HttpMethod(string method) { Method = method; }
    }

    public abstract class HttpContent : IDisposable
    {
        public HttpContentHeaders Headers { get; internal set; } = new HttpContentHeaders();
        public abstract Task<Stream> ReadAsStreamAsync();
        public void Dispose() { }
    }

    public sealed class StringContent : HttpContent
    {
        internal readonly byte[] Buffer;
        internal readonly string ContentTypeValue;
        public StringContent(string content, Encoding encoding, string mediaType)
        {
            Buffer = encoding.GetBytes(content ?? "");
            ContentTypeValue = mediaType + "; charset=" + encoding.WebName;
        }
        public override Task<Stream> ReadAsStreamAsync()
        {
            var completion = new TaskCompletionSource<Stream>();
            completion.TrySetResult(new MemoryStream(Buffer, writable: false));
            return completion.Task;
        }
    }

    internal sealed class ResponseContent : HttpContent
    {
        private Stream? _stream;
        public ResponseContent() { }
        public void Initialize(Stream stream) { _stream = stream; }
        public override Task<Stream> ReadAsStreamAsync()
        {
            var completion = new TaskCompletionSource<Stream>();
            if (_stream != null) completion.TrySetResult(_stream);
            else completion.TrySetException(new IOException("响应流不可用（可能已被取消或释放）。"));
            return completion.Task;
        }
    }

    public sealed class HttpRequestMessage : IDisposable
    {
        public HttpMethod Method { get; }
        public Uri RequestUri { get; }
        public HttpContent? Content { get; set; }
        public HttpRequestMessage(HttpMethod method, Uri requestUri) { Method = method; RequestUri = requestUri; }
        public HttpRequestMessage(HttpMethod method, string requestUri) { Method = method; RequestUri = new Uri(requestUri, UriKind.RelativeOrAbsolute); }
        public void Dispose() { }
    }

    public sealed class HttpResponseMessage : IDisposable
    {
        public HttpStatusCode StatusCode { get; internal set; }
        public bool IsSuccessStatusCode => (int)StatusCode >= 200 && (int)StatusCode <= 299;
        public void EnsureSuccessStatusCode()
        {
            if (!IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)StatusCode);
        }
        public HttpResponseHeaders Headers { get; internal set; } = new HttpResponseHeaders();
        public HttpContent Content { get; internal set; } = new StringContent("", Encoding.UTF8, "text/plain");
        public void Dispose() { }
    }

    public class HttpMessageHandler : IDisposable
    {
        public void Dispose() { }
    }

    public class HttpClientHandler : HttpMessageHandler
    {
        public bool UseProxy { get; set; } = true;
        public IWebProxy? Proxy { get; set; }
        public bool AllowAutoRedirect { get; set; } = true;
    }

    public class HttpClient : IDisposable
    {
        private readonly HttpClientHandler _handler;
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);
        public Uri? BaseAddress { get; set; }
        public HttpRequestHeaders DefaultRequestHeaders { get; } = new HttpRequestHeaders();

        public HttpClient() : this(new HttpClientHandler()) { }
        public HttpClient(HttpMessageHandler handler)
        {
            _handler = handler as HttpClientHandler ?? new HttpClientHandler();
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) =>
            SendAsync(request, HttpCompletionOption.ResponseContentRead, CancellationToken.None);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option) =>
            SendAsync(request, option, CancellationToken.None);

        public Task<HttpResponseMessage> GetAsync(Uri address, HttpCompletionOption option, CancellationToken token) =>
            SendAsync(new HttpRequestMessage(HttpMethod.Get, address), option, token);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken token)
        {
            if (token.IsCancellationRequested) return Cancelled();
            var logical = WorkAssistant.Compatibility.Xp.XpLogicalContext.Capture();
            var completion = new TaskCompletionSource<HttpResponseMessage>();
            try
            {
                var webRequest = BuildRequest(request);
                // R04 修复：Abort 注册存活到响应头返回（完成/异常/取消任一终结时释放）；
                // 请求体写入阶段的取消同样触发 Abort。响应体的取消由调用方按既有约定关流处理。
                var registration = token.Register(() => { try { webRequest.Abort(); } catch { } });
                var released = false;
                var gate = new object();
                Action release = () =>
                {
                    lock (gate) { if (released) return; released = true; }
                    registration.Dispose();
                };
                completion.Task.ContinueWith(_ => release(), TaskScheduler.Default);
                try
                {
                    Send(webRequest, request, token, logical, completion);
                }
                catch
                {
                    release();
                    throw;
                }
            }
            catch (WebException ex)
            {
                var fromStatus = FromWebException(ex, request);
                if (fromStatus != null) completion.TrySetResult(fromStatus);
                else completion.TrySetException(new HttpRequestException(ex.Message, ex));
            }
            catch (Exception ex)
            {
                completion.TrySetException(new HttpRequestException(ex.Message, ex));
            }
            return completion.Task;
        }

        private HttpWebRequest BuildRequest(HttpRequestMessage message)
        {
            if (message.RequestUri == null) throw new InvalidOperationException("缺少请求地址。");
            var address = message.RequestUri.IsAbsoluteUri ? message.RequestUri
                : BaseAddress == null ? throw new InvalidOperationException("相对地址缺少 BaseAddress。")
                : new Uri(BaseAddress, message.RequestUri);
            var request = (HttpWebRequest)WebRequest.Create(address);
            request.Method = message.Method.Method;
            request.AllowAutoRedirect = _handler.AllowAutoRedirect;
            request.AutomaticDecompression = DecompressionMethods.None;
            request.Timeout = Timeout.TotalMilliseconds < 0
                ? System.Threading.Timeout.Infinite
                : (int)Timeout.TotalMilliseconds;
            request.ReadWriteTimeout = System.Threading.Timeout.Infinite;
            // S05：代理三条路径分开——关闭代理（显式 null，绕过系统默认）；
            // 系统默认代理（UseProxy=true 且未指定，保持 request.Proxy 未设置）；
            // 指定代理（赋给请求）。
            if (!_handler.UseProxy) request.Proxy = null;
            else if (_handler.Proxy != null) request.Proxy = _handler.Proxy;
            var authorization = DefaultRequestHeaders.Authorization;
            if (authorization != null)
            {
                request.Headers[HttpRequestHeader.Authorization] =
                    authorization.Parameter == null ? authorization.Scheme : authorization.Scheme + " " + authorization.Parameter;
            }
            foreach (var pair in DefaultRequestHeaders.Extra)
            {
                request.Headers[pair.Key] = pair.Value;
            }
            if (message.Content is StringContent content)
            {
                request.ContentType = content.ContentTypeValue;
                request.ContentLength = content.Buffer.Length;
            }
            return request;
        }

        private void Send(HttpWebRequest request, HttpRequestMessage message, CancellationToken token,
            object? logical, TaskCompletionSource<HttpResponseMessage> completion)
        {
            var content = message.Content as StringContent;
            if (content != null && content.Buffer.Length > 0)
            {
                Task.Factory.FromAsync<Stream>(request.BeginGetRequestStream, request.EndGetRequestStream, null)
                    .ContinueWith(streamTask =>
                    {
                        try
                        {
                            if (streamTask.IsFaulted)
                            {
                                var mapped = FromWebException(streamTask.Exception!.InnerExceptions[0] as WebException, message);
                                if (mapped != null) { completion.TrySetResult(mapped); return; }
                                completion.TrySetException(new HttpRequestException("无法连接模型服务。", streamTask.Exception.InnerExceptions[0]));
                                return;
                            }
                            using var stream = streamTask.Result;
                            stream.Write(content.Buffer, 0, content.Buffer.Length);
                            Respond(request, message, token, logical, completion);
                        }
                        catch (Exception ex) { completion.TrySetException(new HttpRequestException(ex.Message, ex)); }
                    }, TaskScheduler.Default);
            }
            else
            {
                Respond(request, message, token, logical, completion);
            }
        }

        private void Respond(HttpWebRequest request, HttpRequestMessage message, CancellationToken token,
            object? logical, TaskCompletionSource<HttpResponseMessage> completion)
        {
            Task.Factory.FromAsync<WebResponse>(request.BeginGetResponse, request.EndGetResponse, null)
                .ContinueWith(responseTask =>
                {
                    try
                    {
                        if (responseTask.IsFaulted)
                        {
                            var mapped = FromWebException(responseTask.Exception!.InnerExceptions[0] as WebException, message);
                            if (mapped != null) { completion.TrySetResult(mapped); return; }
                            completion.TrySetException(new HttpRequestException("无法连接模型服务。", responseTask.Exception.InnerExceptions[0]));
                            return;
                        }
                        if (responseTask.IsCanceled)
                        {
                            if (token.IsCancellationRequested) completion.TrySetCanceled();
                            else completion.TrySetException(new HttpRequestException("请求已中止。"));
                            return;
                        }
                        var saved = WorkAssistant.Compatibility.Xp.XpLogicalContext.Swap(logical);
                        try { completion.TrySetResult(CreateResponse((HttpWebResponse)responseTask.Result, message)); }
                        finally { WorkAssistant.Compatibility.Xp.XpLogicalContext.Restore(saved); }
                    }
                    catch (Exception ex) { completion.TrySetException(new HttpRequestException(ex.Message, ex)); }
                }, TaskScheduler.Default);
        }

        private static HttpResponseMessage CreateResponse(HttpWebResponse web, HttpRequestMessage message)
        {
            var content = new ResponseContent();
            content.Initialize(web.GetResponseStream()!);
            if (!string.IsNullOrEmpty(web.ContentType))
                content.Headers.ContentType = new Headers.MediaTypeHeaderValue(ParseMediaType(web.ContentType));
            var response = new HttpResponseMessage
            {
                StatusCode = web.StatusCode,
                Content = content,
                Headers = new HttpResponseHeaders(web.Headers),
            };
            return response;
        }

        private static string ParseMediaType(string contentType)
        {
            var separator = contentType.IndexOf(';');
            return (separator < 0 ? contentType : contentType.Substring(0, separator)).Trim();
        }

        private static HttpResponseMessage? FromWebException(WebException? exception, HttpRequestMessage message)
        {
            if (exception == null) return null;
            if (exception.Response is HttpWebResponse web)
            {
                return CreateResponse(web, message);
            }
            return null;
        }

        public void Dispose() { }

        private static Task<HttpResponseMessage> Cancelled()
        {
            var source = new TaskCompletionSource<HttpResponseMessage>();
            source.TrySetCanceled();
            return source.Task;
        }
    }

    public sealed class HttpRequestException : Exception
    {
        public HttpRequestException() { }
        public HttpRequestException(string message) : base(message) { }
        public HttpRequestException(string message, Exception? inner) : base(message, inner) { }
    }
}

namespace System.Net.Http.Headers
{
    public sealed class AuthenticationHeaderValue
    {
        public string Scheme { get; }
        public string? Parameter { get; }
        public AuthenticationHeaderValue(string scheme) { Scheme = scheme; }
        public AuthenticationHeaderValue(string scheme, string parameter) { Scheme = scheme; Parameter = parameter; }
    }

    public sealed class RetryConditionHeaderValue
    {
        public TimeSpan? Delta { get; }
        public DateTimeOffset? Date { get; }
        public RetryConditionHeaderValue(TimeSpan delta) { Delta = delta; }
        public RetryConditionHeaderValue(DateTimeOffset date) { Date = date; }
    }

    public sealed class MediaTypeHeaderValue
    {
        public string MediaType { get; }
        public MediaTypeHeaderValue(string mediaType) { MediaType = SplitMediaType(mediaType); }
        internal static string SplitMediaType(string contentType)
        {
            var separator = contentType.IndexOf(';');
            return (separator < 0 ? contentType : contentType.Substring(0, separator)).Trim();
        }
    }

    public sealed class HttpResponseHeaders
    {
        private readonly WebHeaderCollection? _collection;

        public HttpResponseHeaders() { }
        public HttpResponseHeaders(WebHeaderCollection collection) { _collection = collection; }

        public RetryConditionHeaderValue? RetryAfter
        {
            get
            {
                string? value = _collection != null ? _collection[HttpResponseHeader.RetryAfter] : null;
                if (string.IsNullOrEmpty(value)) return null;
                if (int.TryParse(value!.Trim(), out var seconds)) return new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
                    return new RetryConditionHeaderValue(date);
                return null;
            }
        }

        public IEnumerable<string> GetValues(string name)
        {
            var values = _collection != null ? _collection.GetValues(name) : null;
            return values ?? new string[0];
        }

        public bool TryGetValues(string name, out IEnumerable<string> values)
        {
            var found = _collection != null ? _collection.GetValues(name) : null;
            values = found ?? new string[0];
            return found != null;
        }
    }

    public sealed class HttpRequestHeaders
    {
        internal readonly List<KeyValuePair<string, string>> Extra = new List<KeyValuePair<string, string>>();
        public AuthenticationHeaderValue? Authorization { get; set; }
        public void Add(string name, string value) => Extra.Add(new KeyValuePair<string, string>(name, value));
    }

    public sealed class HttpContentHeaders
    {
        public MediaTypeHeaderValue? ContentType { get; internal set; }
    }
}
